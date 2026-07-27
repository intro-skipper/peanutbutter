# Peanut Butter Plugin Installer

Peanut Butter and Jellyfin. It couldn't be more obvious.

Peanut Butter Plugin Installer adds an administrator-only upload endpoint for installing and updating Jellyfin plugin ZIP archives or standalone DLLs over the network. It stages and validates the upload before replacing anything in Jellyfin's plugin directory.

## Usage

After installing this plugin, open its page from Jellyfin Dashboard → Plugins. Drop a plugin ZIP or DLL onto the upload area, or click it to open file selection. Restart Jellyfin after a successful install or update.

The same operation can be scripted with an authenticated Jellyfin API request:

```powershell
curl.exe -X POST `
  -H "X-Emby-Token: YOUR_ADMIN_API_KEY_OR_TOKEN" `
  -F "file=@C:\path\to\plugin.zip" `
  https://jellyfin.example.com/Plugins/PeanutButter/Install
```

Archives should be standard Jellyfin plugin packages containing at least one DLL and, preferably, `meta.json`. One-off ZIPs containing a DLL are also accepted. When updating an officially installed plugin with a manifestless workflow artifact, Peanut Butter retains the installed manifest and missing supporting files while replacing the uploaded assembly. Standalone DLL uploads are verified as managed assemblies containing a public concrete type implementing Jellyfin's `IPlugin` interface, and all assembly types must load successfully for the current Jellyfin version before installation. This is a structural compatibility check, not a malware scanner or a replacement for trusting the plugin source.

The plugin matches updates by the archive's `guid`; when metadata is missing it falls back to the plugin folder/name or DLL assembly name. ZIP paths are normalized and checked for traversal, archives are limited to 100 MB compressed and 500 MB uncompressed, and the endpoint requires Jellyfin's `RequiresElevation` policy.

## Build

The project targets .NET 10 and Jellyfin 12.x APIs. The GitHub Actions workflows follow the [SkipMe.db plugin workflow split](https://github.com/intro-skipper/skipme.db-plugin/tree/main/.github/workflows): `Build Plugin` builds and uploads the raw DLL for every branch/PR, while `Release Plugin` manually versions, packages, and publishes a release ZIP.

```text
dotnet publish -c Release
```

The generated plugin assembly is `Jellyfin.Plugin.PeanutButter.dll`.
