// Lets a Unity WebGL game tell the Stellar website it was completed (lights that game's star).
mergeInto(LibraryManager.library, {
  StellarMarkComplete: function (game) {
    try { localStorage.setItem("stellar_" + UTF8ToString(game), "1"); } catch (e) {}
  },
});
