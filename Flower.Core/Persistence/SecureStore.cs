namespace Flower.Persistence
{
    // Somewhere to keep what has to outlive the app itself: named text values in
    // the OS's own credential store, which on iOS is the Keychain - and a
    // Keychain item, unlike everything under the app's data directory, survives
    // the app being deleted and installed again.
    //
    // That is the whole reason it exists. A device is paired by its key
    // (DeviceKeyStore) and by what it remembers about its server (PairingBackup),
    // and both used to live only in files. Deleting the app deleted them, so a
    // reinstalled phone came back as a stranger: a new key the server had never
    // seen, no server to find off the home network, and a dead admin entry left
    // behind on the server for the key that no longer existed anywhere.
    //
    // Deliberately small - read, write, delete a string - so a platform
    // implementation is a few lines around its own API and the logic of what
    // goes in and when stays here, shared and testable.
    public interface ISecureStore
    {
        string? Read(string name);

        void Write(string name, string value);

        void Delete(string name);
    }

    // Set by a platform entry point before Avalonia starts, the same way as
    // PlatformDataDirectory. Only iOS sets it. Android's keystore is wiped on
    // uninstall, so it would buy nothing there, and on the desktops the data
    // directory already survives a reinstall.
    public static class PlatformSecureStore
    {
        public static ISecureStore? Current { get; set; }
    }
}
