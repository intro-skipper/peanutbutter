# Peanut Butter

Peanut Butter and Jellyfin. It couldn't be more obvious.

Peanut Butter is a Jellyfin 12 plugin installer for administrators. It installs or updates plugins from ZIP archives and managed DLL files through the Jellyfin dashboard or API.

## Usage

Open **Dashboard → Plugins → Peanut Butter**, then drop a plugin ZIP or DLL onto the upload area, or click it to select a file.

Restart Jellyfin after every successful installation or update. This applies to same-version replacements as well.

Supported uploads:

- Standard Jellyfin plugin ZIP archives.
- ZIP archives containing a plugin DLL without `meta.json`.
- Standalone managed plugin DLLs.

Manifestless updates retain the installed plugin manifest and supporting files while replacing the uploaded assembly. ZIP archives with their own `meta.json` are installed as complete packages.

DLLs are checked for a public concrete implementation of Jellyfin's `IPlugin` interface, and all assembly types must load successfully against Jellyfin 12. These checks verify compatibility and structure; they are not malware scanning.

The API endpoint is:

```text
POST /Plugins/PeanutButter/Install
```

Send the file as a multipart form field named `file` using an authenticated administrator request.

## Limits

- Maximum upload size: 100 MB.
- Maximum extracted ZIP size: 500 MB.
- Maximum ZIP entries: 10,000.

## Build

The project targets .NET 10 and Jellyfin 12.

```text
dotnet publish -c Release
```

The generated assembly is `Jellyfin.Plugin.PeanutButter.dll`.
