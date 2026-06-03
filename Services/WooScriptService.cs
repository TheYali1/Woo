using System.Text;
using System.Text.RegularExpressions;
using Woo_.Models;

namespace Woo_.Services;

public static partial class WooScriptService
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "app.quit",
        "app.restart",
        "app.showMessage",
        "app.log",
        "app.openExternal",
        "app.setBadge",
        "app.setBadgeCount",
        "app.setBadgeDot",
        "app.setBadgeText",
        "app.setBadgeStatus",
        "app.setBadgeIcon",
        "app.setBadgeFromSiteIcon",
        "app.clearBadge",
        "badge.set",
        "badge.count",
        "badge.dot",
        "badge.text",
        "badge.status",
        "badge.icon",
        "badge.siteIcon",
        "badge.clear",
        "window.setTitle",
        "window.resize",
        "window.setWidth",
        "window.setHeight",
        "window.center",
        "window.maximize",
        "window.unmaximize",
        "window.minimize",
        "window.restore",
        "window.fullscreen",
        "window.toggleFullscreen",
        "window.alwaysOnTop",
        "window.setResizable",
        "window.show",
        "window.hide",
        "window.focus",
        "window.blur",
        "window.flash",
        "window.setOpacity",
        "devtools.open",
        "devtools.close",
        "devtools.toggle",
        "page.reload",
        "page.reloadIgnoringCache",
        "page.back",
        "page.forward",
        "page.stop",
        "page.load",
        "page.setZoom",
        "page.getZoom",
        "page.zoomIn",
        "page.zoomOut",
        "page.resetZoom",
        "page.print",
        "page.saveAsPdf",
        "page.screenshot",
        "page.find",
        "page.clearFind",
        "js.run",
        "js.eval",
        "js.file",
        "runjs",
        "inject",
        "css.inject",
        "css.file",
        "css.removeAll",
        "css.hide",
        "css.show",
        "css.theme",
        "page.click",
        "page.clickAll",
        "page.type",
        "page.setValue",
        "page.clear",
        "page.focus",
        "page.blur",
        "page.text",
        "page.html",
        "page.attr",
        "page.setAttr",
        "page.exists",
        "page.waitFor",
        "page.remove",
        "page.scrollTo",
        "page.scrollTop",
        "page.scrollBottom",
        "page.addClass",
        "page.removeClass",
        "page.toggleClass",
        "navigation.block",
        "navigation.allow",
        "navigation.redirect",
        "navigation.openExternal",
        "navigation.lockToMain",
        "navigation.unlock",
        "navigation.setNewLinks",
        "navigation.cancel",
        "downloads.allow",
        "downloads.block",
        "downloads.setFolder",
        "downloads.askWhereToSave",
        "notify",
        "alert",
        "dialog.info",
        "dialog.warning",
        "dialog.error",
        "dialog.confirm",
        "toast",
        "clipboard.writeText",
        "clipboard.readText",
        "clipboard.clear",
        "storage.local.set",
        "storage.local.get",
        "storage.local.remove",
        "storage.local.clear",
        "storage.session.set",
        "storage.session.get",
        "storage.session.remove",
        "storage.session.clear",
        "cookies.set",
        "cookies.get",
        "cookies.remove",
        "cookies.clear",
        "cache.clear",
        "userAgent.set",
        "userAgent.reset",
        "query",
        "queryText",
        "queryHtml",
        "queryAll",
        "setText",
        "setHtml",
        "setStyle",
        "click",
        "type",
        "waitFor",
        "hide",
        "remove"
    };

    private static readonly HashSet<string> TauriUnsupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "devtools.open",
        "devtools.close",
        "devtools.toggle",
        "page.print",
        "page.saveAsPdf",
        "page.screenshot",
        "downloads.setFolder",
        "downloads.askWhereToSave",
        "app.setBadge",
        "app.setBadgeCount",
        "app.setBadgeDot",
        "app.setBadgeText",
        "app.setBadgeStatus",
        "app.setBadgeIcon",
        "app.setBadgeFromSiteIcon",
        "app.clearBadge",
        "badge.set",
        "badge.count",
        "badge.dot",
        "badge.text",
        "badge.status",
        "badge.icon",
        "badge.siteIcon",
        "badge.clear",
        "window.flash"
    };

    public static WooScriptValidationResult Validate(string code, OutputFramework framework)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var diagnostics = new List<WooScriptDiagnostic>();

        if (string.IsNullOrWhiteSpace(code))
        {
            return new WooScriptValidationResult { Success = true };
        }

        var braceBalance = 0;
        var inMultilineString = false;
        var lines = code.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var rawLine = lines[i];
            var line = StripComments(rawLine).Trim();

            var tripleCount = CountOccurrences(line, "\"\"\"");
            var bracePortion = inMultilineString
                ? string.Empty
                : rawLine.Split("\"\"\"", StringSplitOptions.None)[0];
            var commandPortion = inMultilineString
                ? string.Empty
                : line.Split("\"\"\"", StringSplitOptions.None)[0].Trim();

            if (!string.IsNullOrWhiteSpace(commandPortion))
            {
                ValidateLine(commandPortion, lineNumber, framework, errors, warnings, diagnostics);
            }

            braceBalance += CountVisible(bracePortion, '{');
            braceBalance -= CountVisible(bracePortion, '}');
            if (braceBalance < 0)
            {
                AddError(errors, diagnostics, lineNumber, "Closing brace without a matching opening brace.");
                braceBalance = 0;
            }

            if (tripleCount % 2 == 1)
            {
                inMultilineString = !inMultilineString;
            }
        }

        if (inMultilineString)
        {
            AddError(errors, diagnostics, Math.Max(1, lines.Length), "Script has an unterminated multiline string.");
        }

        if (braceBalance > 0)
        {
            AddError(errors, diagnostics, Math.Max(1, lines.Length), "Script has an unclosed block. Add a closing brace.");
        }

        return new WooScriptValidationResult
        {
            Success = errors.Count == 0,
            Errors = errors,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Diagnostics = diagnostics
        };
    }

    public static string Format(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var indent = 0;
        foreach (var rawLine in code.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            if (line.StartsWith('}'))
            {
                indent = Math.Max(0, indent - 1);
            }

            builder.Append(new string(' ', indent * 2));
            builder.AppendLine(line);

            if (line.EndsWith('{'))
            {
                indent++;
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string GetDocsMarkdown()
    {
        return """"
            # WooScript Docs

            WooScript is a local scripting language for apps generated by Woo!.

            ## Comments

            ```wooscript
            # This is a comment
            // This is also a comment
            :: This is also also a comment
            ```

            ## Quick Example

            ```wooscript
            on app.ready {
              window.setTitle("My Woo App")
              window.center()
              notify("Woo!", "App started")
            }

            on page.ready {
              js.run("""
                console.log("Hello from WooScript");
              """)

              css.inject("""
                body {
                  user-select: none;
                }
              """)
            }
            ```

            ## Events

            ```wooscript
            on app.ready { }
            on app.close { }
            on window.ready { }
            on window.focus { }
            on window.blur { }
            on page.loading { }
            on page.ready { }
            on page.loaded { }
            on page.error { }
            on page.titleChanged { }
            on page.urlChanged { }
            on navigation.start { }
            on navigation.finish { }
            on download.start { }
            on download.finish { }
            on download.error { }
            ```

            ## Conditional Events

            ```wooscript
            on url.match("https://example.com/dashboard*") { }
            on url.contains("dashboard") { }
            on title.match("*Login*") { }
            on title.contains("Inbox") { }
            on selector.exists("#app") { }
            ```

            ## Conditions

            ```wooscript
            if page.url contains "login" { }
            if page.url startsWith "https://example.com" { }
            if page.url endsWith "/dashboard" { }
            if page.url matches "https://*.example.com/*" { }
            if page.title contains "Dashboard" { }
            if page.title == "Home" { }
            if page.title != "Error" { }
            if selector.exists("#login") { }
            if selector.text("#status") contains "Ready" { }
            if window.isMaximized { }
            if app.platform == "windows" { }
            ```

            ## Page And DOM Commands

            ```wooscript
            page.reload()
            page.reloadIgnoringCache()
            page.back()
            page.forward()
            page.stop()
            page.load("https://example.com")
            page.setZoom(1.25)
            page.getZoom()
            page.zoomIn()
            page.zoomOut()
            page.resetZoom()
            page.print()
            page.saveAsPdf("C:/Users/Public/Documents/page.pdf")
            page.screenshot("C:/Users/Public/Pictures/page.png")
            page.find("search text")
            page.clearFind()
            page.click("#selector")
            page.clickAll(".button")
            page.type("#input", "hello")
            page.setValue("#input", "hello")
            page.clear("#input")
            page.focus("#input")
            page.blur("#input")
            page.waitFor("#app", 10000)
            page.remove(".ads")
            page.scrollTo(0, 500)
            page.scrollTop()
            page.scrollBottom()
            page.addClass("#selector", "active")
            page.removeClass("#selector", "active")
            page.toggleClass("#selector", "active")
            page.text("#selector")
            page.html("#selector")
            page.attr("#selector", "href")
            page.setAttr("#selector", "data-woo", "true")
            page.exists("#selector")
            query("#selector")
            queryText("#selector")
            queryHtml("#selector")
            queryAll(".item")
            setText("#selector", "Text")
            setHtml("#selector", "<strong>HTML</strong>")
            setStyle("#selector", "color", "red")
            ```

            ## JavaScript And CSS

            ```wooscript
            js.run("console.log(document.title)")
            js.eval("document.title")
            js.file("C:/path/script.js")
            runjs("console.log('alias')")
            inject("console.log('alias')")

            js.run("""
              document.body.dataset.woo = "true";
            """)

            css.inject("""
              body {
                background: #111;
                color: white;
              }
            """)

            css.hide(".ads")
            css.show(".menu")
            css.file("C:/path/style.css")
            css.theme("dark")
            css.removeAll()
            ```

            ## Window Commands

            ```wooscript
            window.setTitle("New Title")
            window.resize(1280, 800)
            window.setWidth(1280)
            window.setHeight(800)
            window.center()
            window.maximize()
            window.unmaximize()
            window.minimize()
            window.restore()
            window.fullscreen(true)
            window.fullscreen(false)
            window.toggleFullscreen()
            window.alwaysOnTop(true)
            window.setResizable(false)
            window.show()
            window.hide()
            window.focus()
            window.flash()
            window.setOpacity(0.95)
            ```

            ## Navigation And Downloads

            ```wooscript
            navigation.block("https://ads.example.com/*")
            navigation.allow("https://example.com/*")
            navigation.redirect("https://old.com/*", "https://new.com")
            navigation.openExternal("https://external.com/*")
            navigation.lockToMain()
            navigation.unlock()
            navigation.setNewLinks("app")
            navigation.setNewLinks("browser")
            navigation.setNewLinks("block")

            downloads.allow()
            downloads.block()
            downloads.setFolder("C:/Users/Public/Downloads")
            downloads.askWhereToSave(true)
            ```

            ## Notifications And Clipboard

            ```wooscript
            notify("Message")
            notify("Title", "Message")
            alert("Message")
            dialog.info("Title", "Message")
            dialog.warning("Title", "Message")
            dialog.error("Title", "Message")
            dialog.confirm("Title", "Message")
            toast("Saved")
            clipboard.writeText("hello")
            clipboard.readText()
            clipboard.clear()
            ```

            ## Taskbar Badge Commands

            ```wooscript
            app.setBadge(1)
            app.setBadge(99)
            app.setBadge("dot")
            app.setBadge("green")
            app.setBadge("red")
            app.setBadge("yellow")
            app.setBadge("orange")
            app.setBadge("blue")
            app.setBadge("purple")
            app.setBadge("white")
            app.setBadge("loading")
            app.setBadge("sync")
            app.setBadge("recording")
            app.setBadge("muted")
            app.setBadge("live")
            app.setBadge("error")
            app.setBadge("warning")
            app.setBadge("info")
            app.setBadge("lock")
            app.setBadge("unlock")
            app.setBadge("star")
            app.setBadge("fire")
            app.setBadge("time")
            app.setBadge("download")
            app.setBadge("upload")
            app.setBadge("update")
            app.setBadge("battery")
            app.setBadge("playmode")
            app.setBadge("pausemode")
            app.setBadge("alertmode")
            app.setBadge("successmode")
            app.setBadge("gamemode")
            app.setBadge("dnd")
            app.setBadgeCount(5)
            app.setBadgeDot("green")
            app.setBadgeText("NEW")
            app.setBadgeStatus("warning")
            app.setBadgeIcon("C:/path/icon.png")
            app.setBadgeFromSiteIcon()
            app.clearBadge()
            ```

            ## Badge Aliases

            ```wooscript
            badge.set("dot")
            badge.count(3)
            badge.dot("red")
            badge.text("NEW")
            badge.status("successmode")
            badge.icon("C:/path/icon.png")
            badge.siteIcon()
            badge.clear()
            ```

            ## Storage, Cookies, Cache

            ```wooscript
            storage.local.set("key", "value")
            storage.local.get("key")
            storage.local.remove("key")
            storage.local.clear()
            storage.session.set("key", "value")
            storage.session.get("key")
            storage.session.remove("key")
            storage.session.clear()
            cookies.set("name", "value")
            cookies.get("name")
            cookies.remove("name")
            cookies.clear()
            cache.clear()
            userAgent.set("Mozilla/5.0")
            userAgent.reset()
            ```

            ## Shortcuts

            ```wooscript
            shortcut "Ctrl+Shift+D" {
              devtools.toggle()
            }

            shortcut "Alt+Left" {
              page.back()
            }
            ```

            ## Timers

            ```wooscript
            wait 500ms
            wait 2s

            every 5s {
              app.log("Still alive")
            }

            after 3s {
              notify("Woo!", "Ready")
            }
            ```

            ## Full Examples

            ```wooscript
            on title.contains("Login") {
              window.resize(900, 700)
              window.center()
              notify("Login detected")
            }

            on url.match("https://example.com/dashboard*") {
              window.setTitle("Dashboard")
              css.hide(".sidebar-ad")
            }

            on page.ready {
              wait 1s

              if selector.exists("#search") {
                page.type("#search", "hello from Woo")
              }
            }
            ```

            """";
    }

    private static void ValidateLine(
        string line,
        int lineNumber,
        OutputFramework framework,
        List<string> errors,
        List<string> warnings,
        List<WooScriptDiagnostic> diagnostics)
    {
        if (line is "{" or "}" || line.StartsWith("} else", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (line.StartsWith("let ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (line.StartsWith("wait ", StringComparison.OrdinalIgnoreCase))
        {
            var duration = line[5..].Trim();
            if (!DurationRegex().IsMatch(duration))
            {
                AddError(errors, diagnostics, lineNumber, "Invalid wait duration. Use 500ms, 2s, or 1m.");
            }

            return;
        }

        if (IsBlockStart(line) || IsInlineEmptyBlock(line))
        {
            ValidateBlockStart(line, lineNumber, errors, diagnostics);
            return;
        }

        var command = ExtractCommandName(line);
        if (string.IsNullOrWhiteSpace(command))
        {
            AddError(errors, diagnostics, lineNumber, "Could not understand this line.");
            return;
        }

        if (!KnownCommands.Contains(command))
        {
            AddError(errors, diagnostics, lineNumber, $"Unknown command '{command}'.");
            return;
        }

        if (framework == OutputFramework.Tauri && TauriUnsupportedCommands.Contains(command))
        {
            AddWarning(warnings, diagnostics, lineNumber, $"{command} is currently Electron-only and will be ignored in Tauri.");
        }
    }

    private static void ValidateBlockStart(
        string line,
        int lineNumber,
        List<string> errors,
        List<WooScriptDiagnostic> diagnostics)
    {
        if (line.StartsWith("every ", StringComparison.OrdinalIgnoreCase))
        {
            var duration = line[6..line.IndexOf('{')].Trim();
            if (!DurationRegex().IsMatch(duration))
            {
                AddError(errors, diagnostics, lineNumber, "Invalid every duration. Use 500ms, 2s, or 1m.");
            }
        }

        if (line.StartsWith("after ", StringComparison.OrdinalIgnoreCase))
        {
            var duration = line[6..line.IndexOf('{')].Trim();
            if (!DurationRegex().IsMatch(duration))
            {
                AddError(errors, diagnostics, lineNumber, "Invalid after duration. Use 500ms, 2s, or 1m.");
            }
        }
    }

    private static void AddError(
        List<string> errors,
        List<WooScriptDiagnostic> diagnostics,
        int line,
        string message)
    {
        errors.Add($"Line {line}: {message}");
        diagnostics.Add(new WooScriptDiagnostic
        {
            Line = Math.Max(1, line),
            Message = message,
            IsWarning = false
        });
    }

    private static void AddWarning(
        List<string> warnings,
        List<WooScriptDiagnostic> diagnostics,
        int line,
        string message)
    {
        warnings.Add($"Line {line}: {message}");
        diagnostics.Add(new WooScriptDiagnostic
        {
            Line = Math.Max(1, line),
            Message = message,
            IsWarning = true
        });
    }

    private static bool IsBlockStart(string line)
    {
        return BlockStartRegex().IsMatch(line);
    }

    private static bool IsInlineEmptyBlock(string line)
    {
        return InlineEmptyBlockRegex().IsMatch(line);
    }

    private static string ExtractCommandName(string line)
    {
        var match = CommandNameRegex().Match(line);
        return match.Success ? match.Groups["name"].Value : string.Empty;
    }

    private static string StripComments(string line)
    {
        var inString = false;
        var inTriple = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (!inString && line.AsSpan(i).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                inTriple = !inTriple;
                i += 2;
                continue;
            }

            var current = line[i];
            if (!inTriple && current == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString || inTriple)
            {
                continue;
            }

            if (current == '#' && IsCommentBoundary(line, i))
            {
                return line[..i];
            }

            if (current == '/' && i + 1 < line.Length && line[i + 1] == '/' && IsCommentBoundary(line, i))
            {
                return line[..i];
            }

            if (current == ':' && i + 1 < line.Length && line[i + 1] == ':' && IsCommentBoundary(line, i))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static bool IsCommentBoundary(string line, int index)
    {
        return index == 0 || char.IsWhiteSpace(line[index - 1]);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static int CountVisible(string value, char target)
    {
        var count = 0;
        var inString = false;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current == '"' && (i == 0 || value[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == target)
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"^(on|if|every|after|shortcut)\b.*\{\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BlockStartRegex();

    [GeneratedRegex(@"^(on|if|every|after|shortcut)\b.*\{\s*\}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEmptyBlockRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*(?:\(|\b)")]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex(@"^\d+(ms|s|m)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();
}
