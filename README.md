# Peanut Butter


<div align="center">
    <p>
        <img alt="Plugin Banner" src="https://raw.githubusercontent.com/intro-skipper/peanutbutter/main/images/logo.png" />
    </p>
</div>

Peanut Butter is a Jellyfin 12 plugin installer for administrators. It installs or updates plugins from ZIP archives and managed DLL files through the Jellyfin dashboard or API, and can fetch those files directly from GitHub releases.

## Usage

Open **Dashboard → Plugins → Peanut Butter**, then drop a plugin ZIP or DLL onto the upload area, or click it to select a file.

Restart Jellyfin after every successful installation or update. The installer page provides a **Restart Jellyfin now** button, so you do not need to return to the dashboard. This applies to same-version replacements as well.

Supported uploads:

- Standard Jellyfin plugin ZIP archives.
- ZIP archives containing a plugin DLL without `meta.json`.
- Standalone managed plugin DLLs.

Manifestless updates retain the installed plugin manifest and supporting files while replacing the uploaded assembly. ZIP archives with their own `meta.json` are installed as complete packages.

Version handling follows Jellyfin's plugin update layout:

- A newer version is installed in a new versioned plugin folder.
- The same version replaces the existing plugin folder.
- An older version requires explicit confirmation before it replaces the installed version.

DLLs are checked for a public concrete implementation of Jellyfin's `IPlugin` interface, and all assembly types must load successfully against Jellyfin 12. These checks verify compatibility and structure; they are not malware scanning.

The API endpoint is:

```text
POST /Plugins/PeanutButter/Install
```

Send the file as a multipart form field named `file` using an authenticated administrator request.

To explicitly approve an older version, retry the request with `?confirmOlderVersion=true`.

## Install from GitHub

The same page can install a plugin straight from a GitHub release: enter `owner/repo` (or paste the repository URL), optionally a tag, fetch the release, pick an asset, and install. The server downloads the asset itself — useful for plugins that publish bare release ZIPs without a repository manifest, and for headless or remote administration.

Every GitHub install is recorded under **Installed from GitHub**, where **Check for updates** compares the installed version against the latest release. Nothing is polled in the background and nothing updates without an explicit administrator action.

Design notes:

- No GitHub token is used or stored. Unauthenticated API access (60 requests/hour per IP) is plenty for admin-initiated installs.
- The server only ever requests URLs it builds itself against `api.github.com`; pasted URLs are parsed for `owner/repo` and discarded. Redirects are followed manually and only to GitHub's own hosts.
- Downloads are capped at the 100 MB upload limit by counted bytes, and verified against the SHA-256 digest GitHub publishes for newer release assets (older assets without a digest install unverified but still pass the full archive validation).
- Downloaded assets go through exactly the same staged validation pipeline as manual uploads, including the downgrade confirmation.

API endpoints (authenticated administrator requests):

```text
POST   /Plugins/PeanutButter/GitHub/Resolve      { "Repository": "owner/repo", "Tag": "optional" }
POST   /Plugins/PeanutButter/GitHub/Install      { "Owner", "Repo", "Tag", "AssetId", "ConfirmOlderVersion" }
GET    /Plugins/PeanutButter/GitHub/Sources
POST   /Plugins/PeanutButter/GitHub/CheckUpdate  { "Owner", "Repo" }
DELETE /Plugins/PeanutButter/GitHub/Sources?owner=...&repo=...
```

After updating Peanut Butter itself, the GitHub feature becomes available on the restart that loads the new version (its HTTP client registers at server startup).

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
