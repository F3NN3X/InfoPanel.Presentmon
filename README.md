# InfoPanel Presentmon Plugin

A high-performance plugin for InfoPanel that provides real-time FPS and graphics performance metrics for fullscreen applications through direct integration with Intel's PresentMonAPI2.dll.

## Features
- **Real-time Performance Monitoring**: Displays FPS, 1% low, 0.1% low, and frame times for fullscreen applications
- **Automatic Application Detection**: Detects active fullscreen applications across multiple monitors using Windows API
- **Direct Native Integration**: Uses PresentMonAPI2.dll for high-performance DirectX/Vulkan metric capture via ETW sessions
- **Event-Driven Architecture**: Real-time sensor updates with minimal CPU overhead
- **Background Processing**: Non-blocking frame data processing with proper async patterns
- **Multi-Monitor Support**: Handles fullscreen detection across different display configurations

## Installation and Setup
Follow these steps to get the InfoPanel Presentmon Plugin working:

1. **Download the Plugin**:
   - Download the latest release ZIP file (`InfoPanel.Presentmon-v2.0.0.zip`) from the [GitHub Releases page](https://github.com/F3NN3X/InfoPanel.Presentmon/releases).

2. **Import the Plugin into InfoPanel**:
   - Open the InfoPanel app.
   - Navigate to the **Plugins** page.
   - Click **Import Plugin Archive**, then select the downloaded ZIP file.
   - InfoPanel will automatically extract and install the plugin with all required dependencies.

3. **Activate the Plugin**:
   - The plugin comes with all necessary files bundled (including `PresentMonAPI2.dll`).
   - No additional downloads or manual configuration required.
   - Simply enable the plugin in InfoPanel's plugin settings.

4. **Start Monitoring**:
   - Launch InfoPanel and the plugin will automatically start.
   - Open any fullscreen application (game, video, etc.).
   - Real-time FPS and performance metrics will appear in InfoPanel.

> **Note**: Version 2.0.0+ no longer requires separate PresentMon executable files. All functionality is integrated directly into the plugin.

## Troubleshooting Steps & Error Messages

If the plugin isn't working as expected, check the InfoPanel logs or console output. Here are common issues and fixes:

### **No FPS Data Showing**
- **What It Means**: The plugin isn't detecting a fullscreen app or ETW session isn't capturing data.
- **How to Fix**:
  - Ensure your app is in **true fullscreen mode** (not windowed or borderless windowed).
  - Try Alt+Enter to toggle fullscreen in games.
  - Check that the application is using DirectX, Vulkan, or OpenGL (required for capture).
  - Verify the plugin is enabled in InfoPanel's plugin settings.

### **Logs Show "ETW Access Denied" or Permission Errors**
- **What It Means**: The plugin can't access Windows Event Tracing sessions.
- **How to Fix**:
  - Add your user account to the **"Performance Log Users"** Windows group:
    1. Run `lusrmgr.msc` as administrator
    2. Go to Groups > Performance Log Users
    3. Add your user account
    4. Log out and back in
  - Alternatively, run InfoPanel as administrator (right-click, **Run as administrator**).

### **Logs Show "Failed to load PresentMonAPI2.dll"**
- **What It Means**: The native DLL couldn't be loaded or found.
- **How to Fix**:
  - Ensure you're running on a 64-bit Windows system.
  - Verify `PresentMonAPI2.dll` is in the plugin folder.
  - Install Visual C++ Redistributable 2019+ (x64) if missing.
  - Re-import the plugin to ensure all files are correctly installed.

### **Plugin Shows as "Error" or "Failed to Load"**
- **What It Means**: The plugin couldn't initialize properly.
- **How to Fix**:
  - Check InfoPanel's plugin logs for specific error messages.
  - Ensure .NET 8.0 runtime is installed on your system.
  - Verify all plugin files are present (DLL, PluginInfo.ini, PresentMonAPI2.dll).

### **General Tips**
- **Restart InfoPanel**: Fixes most initialization issues.
- **Check Application Compatibility**: The plugin works best with games and applications that use modern graphics APIs.
- **Monitor Multiple Apps**: The plugin automatically switches between fullscreen applications.

### **Still Having Issues?**
If problems persist, please open a GitHub Issue with:
- InfoPanel version and plugin version
- Error messages from InfoPanel logs
- Description of the application you're trying to monitor
- Your Windows version and system specifications

## Contributing

Found a bug or have a feature idea? Open an [issue](https://github.com/F3NN3X/InfoPanel.Presentmon/issues) or submit a [pull request](https://github.com/F3NN3X/InfoPanel.Presentmon/pulls) on the repository!

## Development Requirements
- .NET 8.0 SDK
- Visual Studio 2022 or compatible IDE
- Windows 10/11 (x64)
- Dependencies: `Vanara.PInvoke.*` packages for Win32 API access
- InfoPanel.Plugins framework reference
- `PresentMonAPI2.dll` (bundled with plugin)