<div align="center">

<p align="center">
  <img src="https://i.imagesup.co/images2/37581162a513ab54dd3df6edb3c694ee13175ebb.png" alt="Woo Logo" Width="500">
</p>

# Woo!

### Turn websites and local HTML files into desktop apps with Electron or Tauri.

![Woo](https://img.shields.io/badge/Woo%21-Website%20to%20Desktop-8A2BE2?style=for-the-badge)
![Windows](https://img.shields.io/badge/Windows-Supported-0078D4?style=for-the-badge&logo=windows11&logoColor=white)
![Frameworks](https://img.shields.io/badge/Supported_Frameworks-Electron_%26_Tauri-green?style=for-the-badge)

Woo! is a desktop app builder that lets you wrap any website or local HTML file into a clean desktop application.
Choose Electron or Tauri, customize the app behavior, set your own icon, override the User Agent, and build your app with a simple visual workflow.

</div>

---

## Preview

||||
| :---: | :---: | :---: |
| <img src="https://i.imagesup.co/images2/bcfbf373c5464ad3629d7153392ebd4771625c48.png" width="450"><br>**Build Page** | <img src="https://i.imagesup.co/images2/a5f0732ed90f53dec185faba26b946dea1bdfa89.png" width="450"><br>**History Page** | <img src="https://i.imagesup.co/images2/83a11f3488f6efc5ec130b0f473b0ff2370b3729.png" width="450"><br>**Settings Page** |

---

## What is Woo!?

Woo! helps you convert websites and local HTML projects into desktop apps without manually setting up Electron or Tauri projects every time.

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
- View build logs
- View build history

---

## Electron vs Tauri Feature Support

Some features are only available when building with Electron.

| Feature | Electron | Tauri |
| --- | :---: | :---: |
| Website to desktop app | ✅ | ✅ |
| Local HTML to desktop app | ✅ | ✅ |
| Custom icon | ✅ | ✅ |
| Fetch website icon | ✅ | ✅ |
| Fetch website title | ✅ | ✅ |
| User Agent override | ✅ | ✅ |
| Build log | ✅ | ✅ |
| Build history | ✅ | ✅ |
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

### User Agent

You can override the app User Agent to control how websites detect the generated desktop app.

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
It also keeps a build history, making it easier to track previous builds, presets, and output results.

---

## Quick Start

1. Open Woo!
2. Choose a website URL or local HTML file
3. Pick Electron or Tauri
4. Set the app name and icon, or fetch them automatically from the website
5. Configure the app options
6. Build the app
7. Check the build log and output folder

---

## Roadmap Ideas

- Auto update support
- Advanced Tauri options
- Custom splash screen
- Installer customization
- Custom Scripts

---
</div>
