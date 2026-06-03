<div align="center">

<p align="center">
  <img src="https://i.imagesup.co/images2/37581162a513ab54dd3df6edb3c694ee13175ebb.png" alt="Woo Logo" width="500">
</p>

# Woo!

### Turn websites and local HTML files into desktop apps with Electron or Tauri.

![Woo](https://img.shields.io/badge/Woo%21-Website%20to%20Desktop-8A2BE2?style=for-the-badge)
![Version](https://img.shields.io/badge/Version-1.0.1-blue?style=for-the-badge)
![Frameworks](https://img.shields.io/badge/Supported_Frameworks-Electron_%26_Tauri-green?style=for-the-badge)

Woo! is a desktop app builder that lets you wrap any website or local HTML file into a clean desktop application.
Choose Electron or Tauri, customize the app behavior, set your own icon, override the User Agent, add custom scripts, export/import app settings, and build your app with a simple visual workflow.

</div>

---

## Preview

||||
| :---: | :---: | :---: |
| <img src="https://i.imagesup.co/images2/bcfbf373c5464ad3629d7153392ebd4771625c48.png" width="450"><br>**Build Page** | <img src="https://i.imagesup.co/images2/a5f0732ed90f53dec185faba26b946dea1bdfa89.png" width="450"><br>**History Page** | <img src="https://i.imagesup.co/images2/83a11f3488f6efc5ec130b0f473b0ff2370b3729.png" width="450"><br>**Settings Page** |

---

## What is Woo!?

Woo! helps you convert websites and local HTML projects into desktop apps without manually setting up Electron or Tauri projects every time.

It is designed for users who want a fast visual workflow for creating desktop apps from websites, dashboards, tools, games, local HTML projects, and web-based utilities.

---

## Features

- Convert websites into desktop apps
- Convert local HTML files into desktop apps
- Choose between Electron and Tauri builds
- Set a custom app icon
- Fetch the website icon automatically
- Fetch the website title automatically
- Override the User Agent
- Save cookies between sessions
- Enable or disable window resizing
- Start the app maximized
- Show or hide the menu bar
- Enable DevTools for debugging
- Control link redirect behavior
- Lock navigation to the main URL only
- Disable caching
- Allow or block downloading
- Add system tray support in Electron
- Include built-in ad blocker support with uBlock Origin in Electron
- Bundle the output as a single executable in Electron
- Include an installer with the software
- Auto update support
- Check for updates option
- Export app settings
- Import pre-made app settings
- Restart App Export Settings
- custom scripts
- View build logs
- View build history
- Secret easter egg

---

## Electron vs Tauri Feature Support

Some features are only available when building with Electron.

| Feature | Electron | Tauri |
| --- | :---: | :---: |
| System tray | ✅ | ❌ |
| Allow downloading | ✅ | ❌ |
| Built-in ad blocker / uBlock Origin | ✅ | ❌ |
| Bundle as single EXE | ✅ | ❌ |

---

## App Options

Woo! gives you control over the generated desktop app behavior before building.

### Window

| Option | Description |
| --- | --- |
| Window width | Sets the default app window width |
| Window height | Sets the default app window height |
| Allow window resizing | Lets the user resize the app window |
| Start maximized | Opens the app maximized by default |
| Show menu bar | Shows or hides the native app menu bar |
| Enable DevTools | Enables developer tools for debugging |

### Behavior

| Option | Description |
| --- | --- |
| Save cookies | Keeps cookies between app sessions |
| New link redirect | Controls how new links are handled |
| Mouse back/forward navigation | Enables browser-like mouse navigation |
| Lock to main URL only | Prevents navigation outside the main website URL |
| Disable caching | Prevents cache from being saved |
| Allow downloading | Allows files to be downloaded inside the app |
| System tray | Adds tray support where available |

### Packaging

| Option | Description |
| --- | --- |
| Built-in ad blocker | Includes uBlock Origin support in Electron builds |
| Bundle as single EXE | Packages the app as a single executable where supported |
| Include installer | Allows Woo! to be distributed with an installer |

### Custom Scripts

Woo! includes Custom Scripts support, allowing you to customize the behavior of generated apps.

Custom Scripts can be used to inject custom JavaScript, control page behavior, modify app behavior, automate actions, and create more advanced website to desktop workflows.

Woo! also includes a built-in Docs button for Custom Scripts, making it easy to see available commands, and examples directly inside the software.

### User Agent

Woo! allows you to override the app User Agent to control how websites detect the generated desktop app.

---

## After Build Actions

Woo! can run useful actions after the build process finishes:

- Notify when the build finishes
- Open the app after build
- Open the output folder after build
- Create a desktop shortcut after build

---

## Build Log and History

Woo! includes a build log so you can follow what happens during the packaging process.

It also keeps a build history, making it easier to track previous builds, presets, custom scripts, and output results.

---

## Quick Start

1. Open Woo!
2. Choose a website URL or local HTML file
3. Pick Electron or Tauri
4. Set the app name and icon, or fetch them automatically from the website
5. Configure the app options
6. Add a custom script if needed
7. Build the app
---

## Roadmap Ideas

- Advanced Tauri options
- Custom splash screen
- React / Next.js support