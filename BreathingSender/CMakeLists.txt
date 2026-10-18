cmake_minimum_required(VERSION 3.22.1)
project(BreathingUdpSender CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(SMARTSPECTRA_SDK_DIR "$ENV{SMARTSPECTRA_SDK_PATH}")
if (SMARTSPECTRA_SDK_DIR STREQUAL "")
    message(FATAL_ERROR "SMARTSPECTRA_SDK_PATH is not set (point it at the folder you unzipped the SDK into).")
endif ()

list(APPEND CMAKE_PREFIX_PATH "${SMARTSPECTRA_SDK_DIR}")
find_package(SmartSpectra CONFIG REQUIRED)

add_executable(breathing_udp_sender breathing_udp_sender.cpp)
target_link_libraries(breathing_udp_sender SmartSpectra::SDK ws2_32)
