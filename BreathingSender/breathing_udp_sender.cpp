// Streams SmartSpectra (Presage) breathing data to Unity over UDP.
// Built on the documented hello_vitals API:
//   https://smartspectra.presagetech.com/docs/cpp/windows/
//
// Usage:
//   set SMARTSPECTRA_API_KEY=your_key
//   breathing_udp_sender.exe [trace_field] [port] [width] [height] [fps]
//   trace_field = upper_trace (chest, default) or lower_trace (belly)
//   defaults: upper_trace 5005 1280 720 30
//
// Camera choice: on startup the sender lists every camera. If there is more
// than one, it asks which to use and remembers the answer in camera.txt
// (delete that file to choose again). Infrared "Windows Hello" cameras look
// dark, grainy and reddish to Presage, so pick the normal webcam.
//
// Messages sent to 127.0.0.1:<port> (default 5005), one per UDP packet:
//   TRACE:<ts_us>,<value>;<ts_us>,<value>;...   new breathing waveform samples
//   RATE:<bpm>,<confidence 0-100>,<stable 0|1>   Presage breathing rate
//   STATUS:<code>|<name>|<hint>                  validation status (0 = OK)
//   INFO:<text>                                  sender info message
//   ERROR:<text>                                 Presage error
//   PING:<metrics callbacks>,<trace samples>,<preview frames>   once per second
//   FACE:<cx>,<cy>,<w>,<h>                       where your face is in the camera view (0-1, from
//                                                Presage's face landmarks; x right, y down), ~10/s
//
// Camera preview (so you can see what Presage sees) goes to <port>+1
// (default 5006) as: 'P','V', width(uint16 LE), height(uint16 LE), 0, 0,
// then width*height*3 bytes of RGB, top row first. Max ~10 frames/s.

// Stop <windows.h> (pulled in by winsock2.h) from defining min/max macros that break std::min / std::max.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <smartspectra/messages/metrics.h>
#include <smartspectra/smartspectra.h>
#include <smartspectra/smartspectra_config.h>

#include <google/protobuf/descriptor.h>
#include <google/protobuf/message.h>

#include <winsock2.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <fstream>
#include <chrono>
#include <csignal>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace spectra = presage::smartspectra;
namespace gpb = google::protobuf;
using Clock = std::chrono::steady_clock;

namespace {

volatile std::sig_atomic_t g_stop_requested = 0;
void HandleSignal(int) { g_stop_requested = 1; }

SOCKET g_sock = INVALID_SOCKET;
sockaddr_in g_dest{};
sockaddr_in g_preview_dest{};
std::mutex g_send_mutex;

std::string g_field = "upper_trace";

// Stats shown in the console every 2 seconds.
std::atomic<long long> g_metrics_callbacks{0};
std::atomic<long long> g_trace_samples{0};
std::atomic<long long> g_preview_frames{0};
std::atomic<long long> g_last_trace_ts{0};
std::atomic<bool> g_printed_fields{false};
std::atomic<long long> g_video_frames{0};        // every camera frame (not throttled)
std::atomic<long long> g_breathing_msgs{0};      // metrics callbacks that contained breathing
std::atomic<bool> g_session_error{false};        // Presage reported an error this session
std::atomic<bool> g_printed_preview{false};
std::atomic<int> g_frame_w{0}, g_frame_h{0};     // camera frame size, for face positions in pixels
std::atomic<bool> g_printed_face{false};
std::atomic<long long> g_face_msgs{0};
Clock::time_point g_last_face_time{};

std::mutex g_state_mutex;
std::string g_last_status_msg = "STATUS:-1|Starting|Waiting for first camera check";
std::string g_last_status_human = "(none yet)";
float g_last_rate = -1.f, g_last_conf = -1.f;
bool g_last_stable = false;

Clock::time_point g_last_preview_time{};

void SendRaw(const sockaddr_in& dest, const char* data, int len) {
    std::lock_guard<std::mutex> lock(g_send_mutex);
    sendto(g_sock, data, len, 0, reinterpret_cast<const sockaddr*>(&dest), sizeof(dest));
}
void SendText(const std::string& text) {
    SendRaw(g_dest, text.c_str(), static_cast<int>(text.size()));
}

const char* CodeName(int code) {
    switch (code) {
        case 0: return "kOk";
        case 1: return "kNoFaceFound";
        case 2: return "kMultipleFacesFound";
        case 3: return "kFaceNotCentered";
        case 4: return "kFaceSizeOutOfRange";
        case 5: return "kTooDark";
        case 6: return "kTooBright";
        case 7: return "kChestNotVisible";
        case 10: return "kCameraTuning";
        case 11: return "kFrameRateTooLow";
        case 12: return "kExcessiveMotion";
        case 13: return "kFaceTooClose";
        case 14: return "kFaceTooFar";
        case 15: return "kFaceTooHigh";
        case 16: return "kFaceTooLow";
        case 17: return "kFaceNotForward";
        default: return "kUnknown";
    }
}

// Strips characters that would break our simple "a|b|c" message format.
std::string Clean(std::string s) {
    for (char& c : s) {
        if (c == '|' || c == '\n' || c == '\r') c = ' ';
    }
    return s;
}


void PrintFieldsOnce(const gpb::Message& breathing) {
    if (g_printed_fields.exchange(true)) return;
    const gpb::Descriptor* d = breathing.GetDescriptor();
    const gpb::Reflection* r = breathing.GetReflection();
    std::cerr << "\n[presage] First breathing metrics received. Fields:\n";
    for (int i = 0; i < d->field_count(); ++i) {
        const gpb::FieldDescriptor* f = d->field(i);
        std::cerr << "  " << f->name();
        if (f->is_repeated()) std::cerr << " (" << r->FieldSize(breathing, f) << " samples)";
        std::cerr << "\n";
    }
    std::cerr << "[presage] Streaming waveform field: '" << g_field << "'\n"
              << "[presage] (If it shows 0 samples for a while, Presage can't see your chest yet.)\n\n";
}

// Sends every NEW sample of a waveform list (deduped by timestamp).
// Uses the SDK's generated protobuf accessors (value(), timestamp()).
template <typename SampleList>
void StreamTrace(const SampleList& list) {
    std::string packet = "TRACE:";
    int in_packet = 0;
    long long last_ts = g_last_trace_ts.load();
    for (const auto& s : list) {
        const float v = static_cast<float>(s.value());
        const long long ts = static_cast<long long>(s.timestamp());
        if (ts != 0 && ts <= last_ts) continue;  // already sent
        if (ts != 0) last_ts = ts;

        char buf[64];
        std::snprintf(buf, sizeof(buf), "%s%lld,%.6f", in_packet ? ";" : "", ts, v);
        packet += buf;
        ++in_packet;
        g_trace_samples++;
        if (in_packet >= 40) {  // keep packets small
            SendText(packet);
            packet = "TRACE:";
            in_packet = 0;
        }
    }
    if (in_packet > 0) SendText(packet);
    g_last_trace_ts = last_ts;
}

template <typename Breathing>
void StreamRate(const Breathing& b) {
    if (b.rate_size() <= 0) return;
    const auto& last = b.rate(b.rate_size() - 1);
    const float v = static_cast<float>(last.value());
    const float conf = static_cast<float>(last.confidence());
    const bool stable = last.stable();
    char buf[96];
    std::snprintf(buf, sizeof(buf), "RATE:%.3f,%.1f,%d", v, conf, stable ? 1 : 0);
    SendText(buf);
    std::lock_guard<std::mutex> lock(g_state_mutex);
    g_last_rate = v;
    g_last_conf = conf;
    g_last_stable = stable;
}

// Where the face is in the camera view: the bounding box of Presage's face landmarks, sent as
// FACE:cx,cy,w,h in 0-1 (x right, y down). The game draws a small box-and-dot guide from it.
template <typename Face>
void StreamFace(const Face& face) {
    const int n = face.landmarks_size();
    if (n <= 0) return;
    const auto& lm = face.landmarks(n - 1);
    const int k = lm.value_size();
    if (k < 3) return;

    auto now = Clock::now();
    if (now - g_last_face_time < std::chrono::milliseconds(100)) return;
    g_last_face_time = now;

    float minX = 1e9f, minY = 1e9f, maxX = -1e9f, maxY = -1e9f;
    for (int i = 0; i < k; ++i) {
        const float x = static_cast<float>(lm.value(i).x()), y = static_cast<float>(lm.value(i).y());
        minX = std::min(minX, x); maxX = std::max(maxX, x);
        minY = std::min(minY, y); maxY = std::max(maxY, y);
    }
    // Landmarks may come in pixels or already in 0-1; handle both.
    const bool normalized = maxX <= 1.5f && maxY <= 1.5f;
    float fw = static_cast<float>(g_frame_w.load()), fh = static_cast<float>(g_frame_h.load());
    if (normalized) { fw = 1.f; fh = 1.f; }
    if (fw <= 0.f || fh <= 0.f) return;

    if (!g_printed_face.exchange(true)) {
        std::cerr << "[face] Landmarks arriving: " << k << " points, x " << minX << ".." << maxX
                  << ", y " << minY << ".." << maxY << (normalized ? " (0-1)" : " (pixels)") << "\n";
    }
    char buf[128];
    std::snprintf(buf, sizeof(buf), "FACE:%.4f,%.4f,%.4f,%.4f",
                  (minX + maxX) * 0.5f / fw, (minY + maxY) * 0.5f / fh, (maxX - minX) / fw, (maxY - minY) / fh);
    SendText(buf);
    g_face_msgs++;
}

// Downscales a camera frame to ~160px wide RGB and sends it to the preview port.
void SendPreview(const spectra::FrameBuffer& frame) {
    auto now = Clock::now();
    if (now - g_last_preview_time < std::chrono::milliseconds(100)) return;
    g_last_preview_time = now;
    if (!frame.data || frame.width <= 0 || frame.height <= 0) return;

    const int fmt = static_cast<int>(frame.format);
    if (!g_printed_preview.exchange(true)) {
        std::cerr << "[camera] Video frames are arriving: " << frame.width << "x" << frame.height
                  << " (pixel format " << fmt << "). Preview sent to Unity.\n";
    }

    const int w = 160;
    int h = frame.height * w / frame.width;
    if (h < 1) h = 1;
    if (h > 240) h = 240;
    std::vector<char> pkt(8 + w * h * 3);
    pkt[0] = 'P'; pkt[1] = 'V';
    pkt[2] = static_cast<char>(w & 0xff); pkt[3] = static_cast<char>(w >> 8);
    pkt[4] = static_cast<char>(h & 0xff); pkt[5] = static_cast<char>(h >> 8);
    pkt[6] = 0; pkt[7] = 0;

    unsigned char* out = reinterpret_cast<unsigned char*>(pkt.data() + 8);
    for (int y = 0; y < h; ++y) {
        const int sy = y * frame.height / h;
        const uint8_t* row = frame.data + static_cast<size_t>(sy) * frame.stride_bytes;
        for (int x = 0; x < w; ++x) {
            const int sx = x * frame.width / w;
            uint8_t r = 128, g = 128, b = 128;
            switch (fmt) {
                case 0: { const uint8_t* p = row + sx * 3; r = p[0]; g = p[1]; b = p[2]; break; }  // RGB
                case 1: { const uint8_t* p = row + sx * 3; b = p[0]; g = p[1]; r = p[2]; break; }  // BGR
                case 2: { const uint8_t* p = row + sx * 4; r = p[0]; g = p[1]; b = p[2]; break; }  // RGBA
                case 3: { const uint8_t* p = row + sx * 4; b = p[0]; g = p[1]; r = p[2]; break; }  // BGRA
                case 4: case 5: r = g = b = row[sx]; break;                                          // NV12/NV21 (luma)
                case 6: r = g = b = row[sx * 2]; break;                                              // YUYV (luma)
                default: break;
            }
            *out++ = r; *out++ = g; *out++ = b;
        }
    }
    SendRaw(g_preview_dest, pkt.data(), static_cast<int>(pkt.size()));
    g_preview_frames++;
}


std::string Lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

bool LooksInfrared(const std::string& name) {
    const std::string n = Lower(name);
    return n.find(" ir ") != std::string::npos || n.find("infrared") != std::string::npos ||
           n.find("ir camera") != std::string::npos || n.find("hello") != std::string::npos ||
           (n.size() >= 3 && n.compare(n.size() - 3, 3, " ir") == 0) || n.find("ir ") == 0;
}

// Lists cameras, asks the user to pick one (remembered in camera.txt), and
// returns the selection. Falls back to the default camera if listing fails.
spectra::CameraSelection ChooseCamera() {
    std::vector<spectra::CameraInfo> cams;
    const auto err = spectra::SmartSpectra::AvailableCameras(cams);
    if (!err.ok() || cams.empty()) {
        std::cerr << "[camera] Could not list cameras; using the default camera.\n";
        return spectra::CameraSelection::Default();
    }

    std::cerr << "\n[camera] Cameras found:\n";
    int suggested = 0;
    bool suggestion_set = false;
    for (size_t i = 0; i < cams.size(); ++i) {
        const std::string name = cams[i].name.value_or("(no name)");
        const bool ir = LooksInfrared(name);
        std::cerr << "   " << (i + 1) << ") " << name << (ir ? "   <- probably INFRARED, avoid" : "") << "\n";
        if (!ir && !suggestion_set) { suggested = static_cast<int>(i); suggestion_set = true; }
    }

    // Remembered choice (stored by camera id, so plugging in other cameras doesn't change it).
    {
        std::ifstream in("camera.txt");
        std::string saved;
        if (in && std::getline(in, saved) && !saved.empty()) {
            for (size_t i = 0; i < cams.size(); ++i) {
                if (cams[i].id == saved) {
                    std::cerr << "[camera] Using " << (i + 1) << ") " << cams[i].name.value_or("(no name)")
                              << "  (delete camera.txt to choose a different camera)\n\n";
                    return spectra::CameraSelection::ById(cams[i].id);
                }
            }
            std::cerr << "[camera] The camera saved in camera.txt is not connected; choose again.\n";
        }
    }

    int pick = suggested;
    if (cams.size() > 1) {
        std::cerr << "Type the number of the camera to use and press Enter (just Enter = "
                  << (suggested + 1) << "): ";
        std::string line;
        std::getline(std::cin, line);
        try {
            if (!line.empty()) {
                int n = std::stoi(line);
                if (n >= 1 && n <= static_cast<int>(cams.size())) pick = n - 1;
            }
        } catch (...) {}
    }
    std::ofstream("camera.txt") << cams[pick].id << "\n";
    std::cerr << "[camera] Using " << (pick + 1) << ") " << cams[pick].name.value_or("(no name)")
              << "  (saved; delete camera.txt to choose again)\n\n";
    return spectra::CameraSelection::ById(cams[pick].id);
}

}  // namespace

int main(int argc, char** argv) {
    std::signal(SIGINT, HandleSignal);

    const char* key = std::getenv("SMARTSPECTRA_API_KEY");
    if (!key || !*key) {
        std::cerr << "Set SMARTSPECTRA_API_KEY first.\n";
        return 1;
    }
    if (argc > 1) g_field = argv[1];
    int port = argc > 2 ? std::atoi(argv[2]) : 5005;
    int width = argc > 3 ? std::atoi(argv[3]) : 1280;
    int height = argc > 4 ? std::atoi(argv[4]) : 720;
    int fps = argc > 5 ? std::atoi(argv[5]) : 30;

    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 1;
    g_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    int sndbuf = 1 << 20;
    setsockopt(g_sock, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sndbuf), sizeof(sndbuf));
    g_dest.sin_family = AF_INET;
    g_dest.sin_port = htons(static_cast<u_short>(port));
    inet_pton(AF_INET, "127.0.0.1", &g_dest.sin_addr);
    g_preview_dest = g_dest;
    g_preview_dest.sin_port = htons(static_cast<u_short>(port + 1));

    spectra::SmartSpectraConfig config;
    config.api_key = key;
    config.requested_metrics = spectra::SmartSpectraConfig::BreathingMetrics();
    // Also face landmarks (MetricType FACE_LANDMARKS = 11), so the game can show where your head is.
    // Set SMARTSPECTRA_NO_FACE=1 to turn this off.
    if (!std::getenv("SMARTSPECTRA_NO_FACE")) {
        for (const auto& m : spectra::SmartSpectraConfig::FaceMetrics())
            if (static_cast<int>(m) == 11) config.requested_metrics.push_back(m);
    }

    const spectra::CameraSelection camera = ChooseCamera();
    std::cout << "Streaming to 127.0.0.1:" << port << " (preview on " << port + 1
              << "). Press Ctrl+C to stop.\n"
              << "Stats print every 2 s. Presage restarts by itself if it stops.\n";

    int session = 0;
    while (!g_stop_requested) {
        ++session;
        g_session_error = false;
        g_printed_fields = false;
        g_printed_preview = false;

        // A fresh SDK instance each session, so a crash inside Presage can be recovered from.
        spectra::SmartSpectra sdk(config);
        sdk.SetOnMetrics([](const spectra::Metrics& metrics, int64_t) {
            g_metrics_callbacks++;
            if (metrics.has_face()) StreamFace(metrics.face());
            if (!metrics.has_breathing()) return;
            g_breathing_msgs++;
            const auto& b = metrics.breathing();
            PrintFieldsOnce(b);
            if (g_field == "lower_trace") StreamTrace(b.lower_trace());
            else StreamTrace(b.upper_trace());
            StreamRate(b);
        });
        sdk.SetOnValidationStatusChanged([](const spectra::ValidationStatus& status, int64_t) {
            const int code = static_cast<int>(status.code);
            std::ostringstream hint;
            hint << status.hint;
            std::string msg = "STATUS:" + std::to_string(code) + "|" + CodeName(code) + "|" + Clean(hint.str());
            std::string human = std::string(CodeName(code)) + (hint.str().empty() ? "" : " - " + hint.str());
            {
                std::lock_guard<std::mutex> lock(g_state_mutex);
                g_last_status_msg = msg;
                g_last_status_human = human;
            }
            std::cerr << "[presage] Validation " << code << " " << human << "\n";
            SendText(msg);
        });
        sdk.SetOnError([](const spectra::SmartSpectraError& error) {
            std::ostringstream em;
            em << error.message;
            std::cerr << "[presage] Error [" << static_cast<int>(error.code) << "]: " << em.str() << "\n";
            SendText("ERROR:" + Clean(em.str()));
            g_session_error = true;
        });
        sdk.SetOnVideoOutput([](const spectra::FrameBuffer& frame, int64_t) {
            g_video_frames++;
            g_frame_w = frame.width;
            g_frame_h = frame.height;
            SendPreview(frame);
        });

        const auto source_error =
            sdk.UseCamera(camera).SetResolution(width, height).SetFps(fps).Build();
        if (!source_error.ok()) {
            std::cerr << "Failed to create camera source: " << source_error.message << "\n";
            SendText("ERROR:Failed to create camera source");
            return 1;
        }
        if (const auto err = sdk.Start(); !err.ok()) {
            std::cerr << "Failed to start: " << err.message << "\n"
                      << "If the camera could not be opened, close other apps using it, or delete camera.txt and pick another.\n";
            SendText("ERROR:Presage failed to start (check API key / internet / camera in use)");
            if (session == 1) return 1;
            std::this_thread::sleep_for(std::chrono::seconds(3));
            continue;
        }
        SendText(session == 1 ? "INFO:Camera started - sit back so your head and chest are in view"
                              : "INFO:Camera started - Presage restarted, hold still");

        int ticks = 0;
        long long last_frames = g_video_frames.load();
        auto last_frame_change = Clock::now();
        long long fps_frames = last_frames;
        bool restart = false;
        while (!g_stop_requested && !restart) {
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            ++ticks;

            // Detect Presage stopping: an error was reported, or frames stopped arriving.
            const long long frames_now = g_video_frames.load();
            if (frames_now != last_frames) {
                last_frames = frames_now;
                last_frame_change = Clock::now();
            }
            const auto stalled = Clock::now() - last_frame_change;
            if ((g_session_error && stalled > std::chrono::seconds(2)) || stalled > std::chrono::seconds(8)) {
                restart = true;
            }

            if (ticks % 10 == 0) {
                std::string status_msg;
                {
                    std::lock_guard<std::mutex> lock(g_state_mutex);
                    status_msg = g_last_status_msg;
                }
                // Re-send the current status every second so Unity is always up to date.
                SendText(status_msg);
                char ping[96];
                std::snprintf(ping, sizeof(ping), "PING:%lld,%lld,%lld", g_metrics_callbacks.load(),
                              g_trace_samples.load(), g_video_frames.load());
                SendText(ping);
            }
            if (ticks % 20 == 0) {
                const long long f = g_video_frames.load();
                const double cam_fps = (f - fps_frames) / 2.0;
                fps_frames = f;
                std::lock_guard<std::mutex> lock(g_state_mutex);
                std::cerr << "[stats] camera " << static_cast<int>(cam_fps + 0.5) << " fps"
                          << (cam_fps < 20 ? " (LOW - add light)" : "")
                          << " | breathing msgs: " << g_breathing_msgs.load()
                          << " | face: " << g_face_msgs.load()
                          << " | " << g_field << " samples: " << g_trace_samples.load()
                          << " | rate: ";
                if (g_last_rate >= 0) {
                    std::cerr << g_last_rate << " /min (conf " << g_last_conf << "%"
                              << (g_last_stable ? ", stable" : "") << ")";
                } else {
                    std::cerr << "n/a";
                }
                std::cerr << " | status: " << g_last_status_human << "\n";
            }
        }

        if (const auto err = sdk.Stop(); !err.ok()) {
            std::cerr << "Stop failed: " << err.message << "\n";
        }
        if (restart && !g_stop_requested) {
            std::cerr << "\n[presage] Presage stopped processing. Restarting in 2 seconds...\n\n";
            SendText("INFO:Presage restarting - hold still");
            std::this_thread::sleep_for(std::chrono::seconds(2));
        }
    }

    SendText("INFO:Sender stopped");
    closesocket(g_sock);
    WSACleanup();
    return 0;
}
