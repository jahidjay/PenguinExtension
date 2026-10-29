using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PenguinExtention.Models;
using PenguinExtention.Services;
using Task = System.Threading.Tasks.Task;

namespace PenguinExtention.UI
{
    /// <summary>Penguin MCP window (Alt+U, Shift+M): connect to Unity/Unreal/any MCP server, browse its tools and call them.</summary>
    [Guid("A3C95E27-1B8D-4F40-9E6A-7D2B5C1F8E94")]
    public sealed class McpWindow : ToolWindowPane
    {
        public McpWindow() : base(null)
        {
            Caption = "🐧 Penguin MCP";
            Content = new McpControl();
        }
    }

    /// <summary>
    /// Code-built WPF. Server list from .mcp.json files + enabled packs; Connect starts the stdio process or opens the
    /// HTTP session (only on click — a solution's .mcp.json can name any command). Tools list, JSON arguments
    /// pre-filled from the tool's input schema, Call, and the result.
    /// </summary>
    internal sealed class McpControl : UserControl
    {
        private readonly ComboBox _servers = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _connect;
        private readonly TextBlock _endpoint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 6) };
        private readonly ListBox _tools = new ListBox { MinWidth = 180 };
        private readonly TextBlock _toolInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TextBox _args = Mono(readOnly: false);
        private readonly TextBox _result = Mono(readOnly: true);
        private readonly Button _call;
        private List<McpServerConfig> _configs = new List<McpServerConfig>();
        private CancellationTokenSource _callCts;

        public McpControl()
        {
            PenguinTheme.Apply(this);
            SetResourceReference(BackgroundProperty, "P.Bg");
            SetResourceReference(ForegroundProperty, "P.Text");
            _tools.SetResourceReference(BackgroundProperty, "P.Bg");
            _tools.SetResourceReference(ForegroundProperty, "P.Text");

            _connect = Button("Connect", ToggleConnection, "Start / open the selected server");
            _call = Button("Call tool", CallTool, "Send tools/call with the arguments above");
            var header = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
            var bar = new WrapPanel();
            bar.Children.Add(_servers);
            bar.Children.Add(_connect);
            bar.Children.Add(Button("Refresh tools", () => LoadTools(), "tools/list again"));
            bar.Children.Add(Button("Reload config", ReloadConfig, "Re-read .mcp.json files and pack suggestions"));
            bar.Children.Add(Button("Edit .mcp.json", EditConfig, "Open (or create) the solution's .mcp.json"));
            header.Children.Add(bar);
            header.Children.Add(_endpoint);

            var right = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
            var argsHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            argsHeader.Children.Add(new TextBlock { Text = "Arguments (JSON)", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var top = new StackPanel();
            top.Children.Add(_toolInfo);
            top.Children.Add(argsHeader);
            _args.Height = 140;
            top.Children.Add(_args);
            var callBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            callBar.Children.Add(_call);
            callBar.Children.Add(Button("Cancel", () => _callCts?.Cancel(), "Stop waiting for the running call"));
            callBar.Children.Add(Button("Copy result", () => { try { Clipboard.SetText(_result.Text ?? ""); } catch (COMException) { } }, "Copy the result text"));
            top.Children.Add(callBar);
            top.Children.Add(new TextBlock { Text = "Result", FontWeight = FontWeights.SemiBold });
            DockPanel.SetDock(top, Dock.Top);
            right.Children.Add(top);
            right.Children.Add(_result);

            var body = new Grid { Margin = new Thickness(10, 4, 10, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(_tools, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(right, 2);
            body.Children.Add(_tools); body.Children.Add(splitter); body.Children.Add(right);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(_status);
            root.Children.Add(body);
            Content = root;

            _servers.SelectionChanged += (s, e) => OnServerSelected();
            _tools.SelectionChanged += (s, e) => OnToolSelected();
            void OnHubChanged() => Dispatcher.BeginInvoke(new Action(UpdateConnectionState));
            void OnPacksLoaded() => Dispatcher.BeginInvoke(new Action(ReloadConfig));
            Loaded += (s, e) =>
            {
                McpHub.Changed -= OnHubChanged; McpHub.Changed += OnHubChanged;
                PackService.Loaded -= OnPacksLoaded; PackService.Loaded += OnPacksLoaded;
                ReloadConfig();
            };
            Unloaded += (s, e) => { McpHub.Changed -= OnHubChanged; PackService.Loaded -= OnPacksLoaded; };
        }

        private McpServerConfig Selected => (_servers.SelectedItem as ComboBoxItem)?.Tag as McpServerConfig;

        private void ReloadConfig()
        {
            var keep = Selected?.Name;
            var solution = PackService.SolutionDirectory;
            Run(async () =>
            {
                var errors = new List<string>();
                var configs = await Task.Run(() => McpConfig.Load(solution, PackService.McpSuggestions.ToList(), errors)).ConfigureAwait(true);
                _configs = configs;
                _servers.Items.Clear();
                foreach (var c in configs)
                    _servers.Items.Add(new ComboBoxItem { Content = c.Name + "  (" + ShortSource(c.Source) + ")", Tag = c });
                var index = configs.FindIndex(c => c.Name == keep);
                _servers.SelectedIndex = configs.Count == 0 ? -1 : Math.Max(0, index);
                if (configs.Count == 0)
                    SetStatus("No MCP servers configured. Click \"Edit .mcp.json\" to add one (Unity: turn on the Unity pack for a suggestion).");
                else if (errors.Count > 0) SetStatus("❌ " + string.Join("\n", errors));
                else SetStatus(configs.Count + " server(s). Pick one and Connect.");
                OnServerSelected();
            });
        }

        private static string ShortSource(string source) =>
            source == null ? "?" : source.StartsWith("pack ") ? source : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(source)) + "\\" + System.IO.Path.GetFileName(source);

        private void OnServerSelected()
        {
            var cfg = Selected;
            _endpoint.Text = cfg == null ? "" : (cfg.IsStdio ? "Runs: " : "URL: ") + cfg.Endpoint + "   — from " + cfg.Source;
            _tools.Items.Clear();
            _toolInfo.Text = "";
            UpdateConnectionState();
            if (McpHub.Get(cfg?.Name) != null) LoadTools();
        }

        private void UpdateConnectionState()
        {
            var connected = McpHub.Get(Selected?.Name) != null;
            _connect.Content = connected ? "Disconnect" : "Connect";
            _call.IsEnabled = connected;
            if (!connected) _tools.Items.Clear();
        }

        private void ToggleConnection()
        {
            var cfg = Selected;
            if (cfg == null) return;
            if (McpHub.Get(cfg.Name) != null)
            {
                McpHub.Disconnect(cfg.Name);
                SetStatus("Disconnected from " + cfg.Name + ".");
                UpdateConnectionState();
                return;
            }
            SetStatus("Connecting to " + cfg.Name + "…");
            _connect.IsEnabled = false;
            var root = PackService.SolutionDirectory;
            Run(async () =>
            {
                try
                {
                    var client = await Task.Run(() => McpHub.ConnectAsync(cfg, root, CancellationToken.None)).ConfigureAwait(true);
                    SetStatus("Connected to " + ((string)client.ServerInfo?["name"] ?? cfg.Name) + " " + (string)client.ServerInfo?["version"] +
                        " (protocol " + client.NegotiatedVersion + ")" + (string.IsNullOrEmpty(client.Instructions) ? "" : "\n" + client.Instructions));
                    UpdateConnectionState();
                    LoadTools();
                }
                catch (Exception ex)
                {
                    SetStatus("❌ " + cfg.Name + ": " + Unwrap(ex).Message + (string.IsNullOrEmpty(cfg.Hint) ? "" : "\n💡 " + cfg.Hint));
                    PenguinLog.Error("MCP " + cfg.Name, Unwrap(ex).Message);
                }
                finally { _connect.IsEnabled = true; }
            });
        }

        private void LoadTools()
        {
            var client = McpHub.Get(Selected?.Name);
            if (client == null) return;
            Run(async () =>
            {
                var tools = await Task.Run(() => client.ListToolsAsync(CancellationToken.None)).ConfigureAwait(true);
                _tools.Items.Clear();
                foreach (var t in tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    _tools.Items.Add(new ListBoxItem { Content = t.ToString(), Tag = t, ToolTip = t.Description });
                SetStatus(client.Config.Name + ": " + tools.Count + " tool(s).");
                if (_tools.Items.Count > 0) _tools.SelectedIndex = 0;
            });
        }

        private void OnToolSelected()
        {
            var tool = (_tools.SelectedItem as ListBoxItem)?.Tag as McpTool;
            if (tool == null) return;
            var required = ((tool.InputSchema?["required"] as JArray) ?? new JArray()).Select(t => (string)t).ToList();
            var props = (tool.InputSchema?["properties"] as JObject)?.Properties()
                .Select(p => p.Name + (required.Contains(p.Name) ? "*" : "") + ": " + ((string)p.Value["type"] ?? "any") +
                    (p.Value["description"] != null ? " — " + (string)p.Value["description"] : "")).ToList() ?? new List<string>();
            _toolInfo.Text = tool.Name + "\n" + tool.Description + (props.Count == 0 ? "\n(no arguments)" : "\n\nArguments (* = required):\n  " + string.Join("\n  ", props));
            _args.Text = McpClient.ArgumentTemplate(tool.InputSchema).ToString(Formatting.Indented);
        }

        private void CallTool()
        {
            var client = McpHub.Get(Selected?.Name);
            var tool = (_tools.SelectedItem as ListBoxItem)?.Tag as McpTool;
            if (client == null || tool == null) { SetStatus("Connect and pick a tool first."); return; }
            JObject args;
            try { args = string.IsNullOrWhiteSpace(_args.Text) ? new JObject() : JObject.Parse(_args.Text); }
            catch (JsonException ex) { SetStatus("❌ Arguments are not a JSON object: " + ex.Message); return; }
            _callCts?.Cancel();
            var cts = new CancellationTokenSource();
            _callCts = cts;
            _call.IsEnabled = false;
            _result.Text = "";
            SetStatus("Calling " + tool.Name + "…");
            var started = DateTime.Now;
            Run(async () =>
            {
                try
                {
                    var result = await Task.Run(() => client.CallToolAsync(tool.Name, args, cts.Token)).ConfigureAwait(true);
                    _result.Text = McpClient.FormatResult(result);
                    SetStatus(tool.Name + " finished in " + (DateTime.Now - started).TotalSeconds.ToString("0.0") + " s" + ((bool?)result["isError"] == true ? " (tool error)" : "."));
                }
                catch (OperationCanceledException) { SetStatus(tool.Name + " cancelled."); }
                finally { _call.IsEnabled = McpHub.Get(client.Config.Name) != null; }
            });
        }

        private void EditConfig()
        {
            var solution = PackService.SolutionDirectory;
            if (solution == null) { SetStatus("Open a solution first (.mcp.json lives in the solution folder)."); return; }
            Run(async () =>
            {
                var path = await Task.Run(() => McpConfig.EnsureSolutionConfig(solution)).ConfigureAwait(true);
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
                SetStatus("Edit " + path + ", save, then click Reload config.");
            });
        }

        private void Run(Func<Task> work)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { await work().ConfigureAwait(true); }
                catch (Exception ex)
                {
                    PenguinLog.Error("MCP", Unwrap(ex));
                    SetStatus("❌ " + Unwrap(ex).Message);
                }
            });
        }

        private static Exception Unwrap(Exception ex)
        {
            while ((ex is AggregateException || ex is System.Reflection.TargetInvocationException) && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        private void SetStatus(string text) => _status.Text = text;

        private static TextBox Mono(bool readOnly)
        {
            var box = new TextBox
            {
                FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, AcceptsTab = true, IsReadOnly = readOnly,
                TextWrapping = readOnly ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            box.SetResourceReference(BackgroundProperty, "P.Control");
            box.SetResourceReference(ForegroundProperty, "P.Text");
            box.SetResourceReference(BorderBrushProperty, "P.ControlBorder");
            return box;
        }

        private static Button Button(string text, Action click, string tip)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
            b.Click += (s, e) => click();
            return b;
        }
    }
}
