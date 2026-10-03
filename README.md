<!-- prettier-ignore-start -->

![Shoko Renamer NN Logo](https://raw.githubusercontent.com/natyusha/ShokoRenamerNN/master/ShokoRenamerNN/Assets/shoko-renamer-nn-logo-small.png "Shoko Renamer NN")  
[![Discord](https://img.shields.io/discord/96234011612958720?logo=discord&logoColor=fff&label=Discord&color=5865F2 "Shoko Discord")](https://discord.gg/shokoanime)
[![Shoko Docs](https://img.shields.io/badge/VitePress-Shoko_Docs-4E7CF5?logo=vitepress&logoColor=fff)](https://docs.shokoanime.com/)
[![GitHub Latest](https://img.shields.io/github/v/tag/natyusha/ShokoRenamerNN?label=Latest&logo=github&logoColor=fff)](https://github.com/natyusha/ShokoRenamerNN/releases/latest)
-

<!-- prettier-ignore-end -->

This is a custom renamer plugin for [Shoko Server](https://shokoanime.com/) which uses a strict AniDB only naming scheme within a flat folder structure.

## Installation

Installation can be completed via Shoko's WebUI (Recommended) or Manually. Both methods are detailed below:

- **WebUI** (Recommended)
  - Open Shoko's WebUI and navigate to: `Settings > Plugin Management > Repositories`
  - Click `Add Repository` and configure the following:
    - Name: `NN Plugins`
    - Manifest URL: `https://raw.githubusercontent.com/natyusha/ShokoPluginManifest/master/manifest.json`
  - Go to `Settings > Plugin Management > Browse` and find "Shoko Renamer NN"
  - Click `Install`
- **Manual**
  - Navigate to Shoko Server's `plugins` directory and create a new subfolder called `ShokoRenamerNN`
  - Extract [the latest release](https://github.com/natyusha/ShokoRenamerNN/releases) into the `plugins/ShokoRenamerNN` directory
  - It may be necessary to create the `plugins` (all lowercase) folder in Shoko's root first
- Restart Shoko Server after finishing either of the above installation methods

## Usage

1. Navigate to `Utilities > File Rename` in Shoko's WebUI
2. Click the `+` button to add a preset under "Preset Selection" and select "Shoko Renamer NN" as the provider
3. Give the preset an appropriate name, then click `Save`
4. With the new preset selected make sure to configure the "Main Folder Name" and "18 Restricted Folder Name"
5. Disable any of the advanced formatting or overrides that aren't desired, then click `Save`

## File Naming

Files will be organized and named as follows:

`{destination}/{folderName}/{title} - {epNumber}{fileTags}.{extension}`

- `destination`: Automatically routed to the configured `Main Folder Name` or `18 Restricted Folder Name`
  - The names should exactly match a managed folder in Shoko
- `folderName`: Formatted `title` with common prefixes shifted to the end after an em dash (`—`)
  - Common prefixes include things like: `OVA` or `Gekijouban`
- `title`: The "Main" series title on AniDB
  - This is the romanised version of an anime's original language official title
- `epNumber`: Padded episode numbers with range formatting, relation indicators, or single-entry titles
  - Relation indicators will be appended in parenthesis like so: `O1 (E01)`
  - Single-entry titles will replace the episode number like so: `(Complete Movie)`
- `fileTags`: Optional preserved release tags from the source file
  - Release tags include things like: `[uncen]` or `[raw]`
- `extension`: The file extension

> [!NOTE]
> Appending common prefixes, using single-entry titles, and preserving release tags can all be disabled in the settings if desired.
