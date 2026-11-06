using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Trio.SharedLibrary;
using Trio.SharedLibrary.Functionalities;

namespace RTSys.PlugIn
{
    internal class ChatPanel : UserControl, IToolControl
    {
        private readonly ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private ScrollViewer _scrollViewer;
        private TextBox _inputBox;
        private Button _sendBtn;
        private AiService _ai;
        private bool _isProcessing;
        private DockPanel _inputPanel;
        private Border _confirmPanel;
        private TaskCompletionSource<bool> _confirmTcs;
        private readonly object _confirmLock = new object();

        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private ChatMessage _streamingMsg;
        private System.Threading.CancellationTokenSource _cts;
        // 流式 UI 缓冲:累积 delta,按字符数/时间批量 flush,避免每个 delta 一次 BeginInvoke 撑爆 UI 队列
        private string _pendingDelta;
        private System.Windows.Threading.DispatcherTimer _flushTimer;
        private const int StreamFlushChars = 160;
        private TextBlock _statusInfo;
        private Border _planModeBanner;
        private Border _researchBanner;
        private ProgressBar _researchBar;
        private TextBlock _researchLabel;

        /// <summary>
        /// Safe MessageBox.Show helper that doesn't crash when Window.GetWindow(this) returns null
        /// (e.g., when ChatPanel is hosted via HwndSource.RootVisual instead of a real Window).
        /// </summary>
        private void ShowMessage(string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            try
            {
                var owner = Window.GetWindow(this);
                if (owner != null)
                {
                    MessageBox.Show(owner, text, caption, button, icon);
                }
                else
                {
                    MessageBox.Show(text, caption, button, icon);
                }
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("ChatPanel.ShowMessage", ex); } catch { }
            }
        }

        /// <summary>
        /// 安全设置对话框 Owner：宿主不在真实 Window 内时 Window.GetWindow(this) 返回 null，
        /// 此时不设置 Owner（保持默认 null），避免抛异常。
        /// </summary>
        private void ApplySafeOwner(Window win)
        {
            try
            {
                var owner = Window.GetWindow(this);
                if (owner != null) win.Owner = owner;
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("ChatPanel.ApplySafeOwner", ex); } catch { }
            }
        }

        /// <summary>
        /// 应用主题色（背景 / 前景 / 强调色）。全部为尽力而为，失败仅记录日志；
        /// 宿主未提供主题时由调用方传入兜底深色。
        /// </summary>
        internal void ApplyTheme(Color background, Color foreground, Color accent)
        {
            try
            {
                var bgBrush = new SolidColorBrush(background);
                var fgBrush = new SolidColorBrush(foreground);
                var accentBrush = new SolidColorBrush(accent);

                Background = bgBrush;
                if (_scrollViewer != null)
                {
                    _scrollViewer.Background = bgBrush;
                    var items = _scrollViewer.Content as Control;
                    if (items != null)
                    {
                        items.Background = bgBrush;
                        items.Foreground = fgBrush;
                    }
                }
                if (_inputBox != null)
                {
                    _inputBox.Background = bgBrush;
                    _inputBox.Foreground = fgBrush;
                }
                if (_sendBtn != null)
                {
                    _sendBtn.Background = accentBrush;
                    _sendBtn.Foreground = fgBrush;
                }
                if (_statusInfo != null)
                {
                    _statusInfo.Foreground = fgBrush;
                    _statusInfo.Opacity = 0.65;
                }
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("ChatPanel.ApplyTheme", ex); } catch { }
            }
        }

        // --- Oscilloscope pattern: static factory ---
        private static readonly ToolPositionSettings _defaultPosition = new ToolPositionSettings(SizeToContent.Manual)
        {
            fwidth = 420.0,
            fheight = 560.0
        };

        private static readonly ChatPanelFactory _factory = new ChatPanelFactory(_defaultPosition);

        public static ToolFactory Factory => (ToolFactory)(object)_factory;
        public static string RegisteredToolName => _factory.ToolName;

        private readonly ChatTool _tool;

        public ITool Tool => (ITool)(object)_tool;
        public IInputElement DefaultFocusedElement => _inputBox;
        public string HelpKeyword => "AI Assistant";

        public ChatPanel()
        {
            try
            {
                _tool = new ChatTool(this);
                InitAiService();
                BuildUI();
                // Prevent unhandled UI thread exceptions from crashing the RTSys process
                System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
                {
                    try { PlugInLog.Log("UI thread unhandled exception: " + e.Exception); } catch { }
                    e.Handled = true;
                };
                _ai.StartNewSession();
                this.Loaded += (s, e) =>
                {
                    LoadLastSession();
                };
            }
            catch (Exception ex)
            {
                try { AiService.LogException("ChatPanel ctor", ex); } catch { }
                throw;
            }
        }

        private void LoadLastSession()
        {
            try
            {
                var sessions = _ai.ListSessions();
                if (sessions.Count == 0) return;
                var last = sessions[0]; // already sorted by time descending
                var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI");
                var path = Path.Combine(dataDir, "chat_history", last.Id + ".json");
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path);
                var data = _json.Deserialize<Dictionary<string, object>>(text);
                _ai.LoadSession(last.Id);
                _messages.Clear();   // 与 LoadSessionMessages 一致，避免重复加载时 UI 消息累积
                object msgsObj;
                if (data.TryGetValue("messages", out msgsObj) && msgsObj is System.Collections.ArrayList al)
                {
                    foreach (var m in al)
                    {
                        var md = m as Dictionary<string, object>;
                        if (md != null)
                        {
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "System";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            var thinking = md.ContainsKey("thinkingText") ? md["thinkingText"]?.ToString() : "";
                            var chatMsg = new ChatMessage(role, msg);
                            if (!string.IsNullOrEmpty(thinking)) chatMsg.ThinkingText = thinking;
                            _messages.Add(chatMsg);
                        }
                    }
                }
                Dispatcher.BeginInvoke(new Action(() => _scrollViewer.ScrollToEnd()));
            }
            catch { }
        }

        private void InitAiService()
        {
            _ai = new AiService();
            _ai.OnAiThinkingStart = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg != null && !string.IsNullOrEmpty(_streamingMsg.ThinkingText))
                        return; // already streaming thinking
                    _streamingMsg = new ChatMessage("AI", "");
                    _messages.Add(_streamingMsg);
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnAiThinkingDelta = (delta) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg != null)
                    {
                        _streamingMsg.ThinkingText = (_streamingMsg.ThinkingText ?? "") + delta;
                        _scrollViewer.ScrollToEnd();
                    }
                }));
            };
            _ai.OnAiThinkingEnd = () =>
            {
                // thinking done; text will follow via OnAiTextStart
            };
            _ai.OnAiTextStart = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg != null && !string.IsNullOrEmpty(_streamingMsg.ThinkingText))
                    {
                        // Reuse existing message (thinking already appended)
                    }
                    else
                    {
                        _streamingMsg = new ChatMessage("AI", "");
                        _messages.Add(_streamingMsg);
                    }
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnAiTextDelta = (delta) =>
            {
                // delta 来自后台线程,用 BeginInvoke 切到 UI 线程后累积到 _pendingDelta,再按字符数/时间批量 flush
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_streamingMsg == null) return;
                    _pendingDelta = (_pendingDelta ?? "") + delta;
                    if (_pendingDelta.Length >= StreamFlushChars)
                    {
                        FlushPendingDelta();
                    }
                    else
                    {
                        if (_flushTimer == null)
                        {
                            _flushTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                            _flushTimer.Tick += (s, e2) => FlushPendingDelta();
                        }
                        _flushTimer.Stop();
                        _flushTimer.Start();
                    }
                }));
            };
            _ai.OnAiTextEnd = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FlushPendingDelta();
                    if (_streamingMsg != null && string.IsNullOrEmpty(_streamingMsg.Text)
                        && string.IsNullOrEmpty(_streamingMsg.ThinkingText))
                    {
                        // Empty bubble — remove it
                        _messages.Remove(_streamingMsg);
                    }
                    _streamingMsg = null;
                    AutoSaveSession();
                }));
            };
            _ai.OnSystemMessage = (text) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _messages.Add(new ChatMessage("System", text));
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnToolStatus = (status) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _messages.Add(new ChatMessage("System", UnescapeJsonDisplay(status)));
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnResearchStart = (agentType, maxTurns) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _researchBar.Maximum = maxTurns;
                    _researchBar.Value = 0;
                    _researchLabel.Text = string.Format(Lang.L("[{0}] 子 agent 正在{1}…", "[{0}] subagent {1}…"), agentType, SubagentVerb(agentType));
                    _researchBanner.Visibility = Visibility.Visible;
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnResearchTurn = (agentType, turn, maxTurns, tools) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _researchBar.Maximum = maxTurns;
                    _researchBar.Value = turn;
                    _researchLabel.Text = string.Format(Lang.L("[{0}] 轮 {1}: {3}", "[{0}] turn {1}: {3}"), agentType, turn, maxTurns, tools);
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnResearchEnd = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _researchBanner.Visibility = Visibility.Collapsed;
                }));
            };
            _ai.OnResearchTrace = (agentType, traceText) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var m = new ChatMessage("Subagent",
                        "[" + agentType + "] " + SubagentVerb(agentType)
                        + " — " + Lang.L("内部轨迹见下方 ▼", "internal trace under ▼ below"));
                    m.ThinkingText = traceText;
                    _messages.Add(m);
                    _scrollViewer.ScrollToEnd();
                }));
            };
            _ai.OnConfirmWrite = (toolName, argsJson) =>
            {
                return ShowInlineConfirmation(toolName, argsJson);
            };
            _ai.OnPlanModeChanged = (active) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _planModeBanner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                }));
            };
            _ai.OnConfirmPlan = (plan) =>
            {
                return ShowPlanApproval(plan);
            };
        }

        /// <summary>
        /// 内嵌 Plan Mode 审批面板：显示 AI 提交的计划文本，用户点「允许」批准 / 「拒绝」保持 Plan Mode。
        /// 复用 _confirmPanel + _confirmTcs + OnConfirmAllow/OnConfirmReject 机制（与 ShowInlineConfirmation 一致）。
        /// 同步等待用户决策（在 worker 线程调用，UI 通过 Dispatcher 切回）。
        /// </summary>
        private bool ShowPlanApproval(string plan)
        {
            lock (_confirmLock)
            {
                var tcs = new TaskCompletionSource<bool>();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var header = Lang.L("📋 AI 在 Plan Mode 下完成调研，提交以下计划（点击「允许」批准 → 退出 Plan Mode；「拒绝」保持 Plan Mode）:\n\n",
                                        "📋 AI completed investigation in Plan Mode, submitted plan below (Allow → exit Plan Mode; Reject → stay in Plan Mode):\n\n");
                    _messages.Add(new ChatMessage("System", header + Truncate(plan, 1500)));
                    _scrollViewer.ScrollToEnd();

                    _inputPanel.Visibility = Visibility.Collapsed;
                    _confirmPanel.Visibility = Visibility.Visible;
                    _confirmTcs = tcs;
                }));
                return tcs.Task.Result;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private void BuildUI()
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            var root = new DockPanel();

            // ---- Top toolbar ----
            var toolbar = new DockPanel { Margin = new Thickness(4, 4, 8, 4), LastChildFill = false };
            toolbar.SetValue(DockPanel.DockProperty, Dock.Top);

            var settingsBtn = new Button
            {
                Content = Lang.S("Settings"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            settingsBtn.Click += OnSettings;
            DockPanel.SetDock(settingsBtn, Dock.Right);
            toolbar.Children.Add(settingsBtn);

            var historyBtn = new Button
            {
                Content = Lang.S("History"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            historyBtn.Click += OnHistory;
            DockPanel.SetDock(historyBtn, Dock.Right);
            toolbar.Children.Add(historyBtn);

            var memoryBtn = new Button
            {
                Content = Lang.S("Memory"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            memoryBtn.Click += OnMemory;
            DockPanel.SetDock(memoryBtn, Dock.Right);
            toolbar.Children.Add(memoryBtn);

            var clearBtn = new Button
            {
                Content = Lang.S("Clear"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            clearBtn.Click += (s, e) =>
            {
                _messages.Clear();
                _ai.StartNewSession();
                UpdateStatusInfo();
            };
            DockPanel.SetDock(clearBtn, Dock.Right);
            toolbar.Children.Add(clearBtn);

            var newBtn = new Button
            {
                Content = Lang.S("New"),
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 90, 160)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            newBtn.Click += (s, e) =>
            {
                if (_messages.Count > 0) AutoSaveSession();
                _messages.Clear();
                _ai.StartNewSession();
                UpdateStatusInfo();
            };
            DockPanel.SetDock(newBtn, Dock.Right);
            toolbar.Children.Add(newBtn);

            _statusInfo = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            toolbar.Children.Add(_statusInfo);

            var aboutBtn = new Button
            {
                Content = "关于",
                Height = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            aboutBtn.Click += OnAbout;
            DockPanel.SetDock(aboutBtn, Dock.Left);
            toolbar.Children.Add(aboutBtn);

            root.Children.Add(toolbar);

            // ---- Plan Mode 状态条（橙色高亮，仅 plan mode 激活时可见）----
            _planModeBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(180, 90, 0)),
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed
            };
            _planModeBanner.SetValue(DockPanel.DockProperty, Dock.Top);
            var bannerText = new TextBlock
            {
                Text = Lang.L("🔒 Plan Mode 活动中 — AI 正在调研，所有写操作（写程序 / 编译 / 运行 / VR / TABLE）已拦截，等待 AI 提交计划给你审批",
                              "🔒 Plan Mode active — AI is investigating. All write operations (programs / compile / run / VR / TABLE) are blocked, waiting for AI to submit a plan for your approval"),
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            _planModeBanner.Child = bannerText;
            root.Children.Add(_planModeBanner);

            // ---- research 子 agent 进度条 banner（蓝色，仅 research 运行时可见）----
            _researchBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 60, 110)),
                Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed
            };
            _researchBanner.SetValue(DockPanel.DockProperty, Dock.Top);
            var researchStack = new StackPanel { Orientation = Orientation.Vertical };
            _researchLabel = new TextBlock
            {
                Text = Lang.L("[research] 子 agent 正在调研…", "[research] subagent investigating…"),
                Foreground = Brushes.White,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _researchBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 12,
                Value = 0,
                Height = 8,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(90, 170, 255))
            };
            researchStack.Children.Add(_researchLabel);
            researchStack.Children.Add(_researchBar);
            _researchBanner.Child = researchStack;
            root.Children.Add(_researchBanner);

            // Top separator
            var topSep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            topSep.SetValue(DockPanel.DockProperty, Dock.Top);
            root.Children.Add(topSep);

            // ---- Bottom input panel ----
            _inputPanel = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
            _inputPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            _sendBtn = new Button
            {
                Content = Lang.S("Send"),
                Width = 60,
                Height = 28,
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            _sendBtn.Click += OnSend;
            DockPanel.SetDock(_sendBtn, Dock.Right);
            _inputPanel.Children.Add(_sendBtn);

            _inputBox = new TextBox
            {
                Height = 80,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Padding = new Thickness(6, 4, 6, 4)
            };
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    OnSend(null, null);
                }
            };
            _inputPanel.Children.Add(_inputBox);
            root.Children.Add(_inputPanel);

            // ---- Confirm panel (hidden, shown when AI needs permission) ----
            _confirmPanel = new Border
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Color.FromRgb(50, 45, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 100, 30)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 4, 8, 4)
            };
            _confirmPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            var confirmDock = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

            var rejectBtn = new Button
            {
                Content = Lang.S("Reject"),
                Width = 65, Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(160, 40, 40)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            rejectBtn.Click += OnConfirmReject;
            DockPanel.SetDock(rejectBtn, Dock.Left);
            confirmDock.Children.Add(rejectBtn);

            var allowBtn = new Button
            {
                Content = Lang.S("Allow"),
                Width = 65, Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 70)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            allowBtn.Click += OnConfirmAllow;
            DockPanel.SetDock(allowBtn, Dock.Left);
            confirmDock.Children.Add(allowBtn);

            var confirmLabel = new TextBlock
            {
                Text = Lang.S("ConfirmMsg"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 50)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12
            };
            confirmDock.Children.Add(confirmLabel);

            _confirmPanel.Child = confirmDock;
            root.Children.Add(_confirmPanel);

            // Bottom separator
            var sep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            sep.SetValue(DockPanel.DockProperty, Dock.Bottom);
            root.Children.Add(sep);

            // ---- Messages area ----
            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var itemsControl = new ItemsControl { ItemsSource = _messages };
            ScrollViewer.SetCanContentScroll(itemsControl, false);
            itemsControl.ItemTemplate = CreateMessageTemplate();
            _scrollViewer.Content = itemsControl;
            root.Children.Add(_scrollViewer);

            Content = root;

            if (!_ai.HasApiKey)
                _messages.Add(new ChatMessage("System", Lang.S("NoApiKey")));
        }

        private DataTemplate CreateMessageTemplate()
        {
            var template = new DataTemplate(typeof(ChatMessage));

            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
            factory.SetValue(Border.MarginProperty, new Thickness(8, 3, 8, 3));
            factory.SetValue(Border.MaxWidthProperty, 600.0);

            factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToBrushConverter() });
            factory.SetBinding(Border.HorizontalAlignmentProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToAlignmentConverter() });

            // StackPanel to hold Expander (thinking) + TextBox (text)
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));

            // Expander for thinking content (collapsed by default, visible only when ThinkingText is not empty)
            var expanderFactory = new FrameworkElementFactory(typeof(Expander));
            expanderFactory.SetValue(Expander.IsExpandedProperty, AiService.ShowThinking);
            expanderFactory.SetValue(Expander.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 45, 45)));
            expanderFactory.SetValue(Expander.ForegroundProperty, new SolidColorBrush(Color.FromRgb(160, 160, 160)));
            expanderFactory.SetValue(Expander.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(70, 70, 70)));
            expanderFactory.SetValue(Expander.MarginProperty, new Thickness(0, 0, 0, 4));
            expanderFactory.SetBinding(Expander.VisibilityProperty,
                new System.Windows.Data.Binding("ThinkingText") { Converter = new ThinkingVisibilityConverter() });

            // Expander header — 按 Role 区分：普通消息显示"思考"，Subagent 轨迹消息显示"子 agent 轨迹"
            expanderFactory.SetBinding(Expander.HeaderProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToHeaderConverter() });

            // Expander content — thinking text
            var thinkingTextFactory = new FrameworkElementFactory(typeof(TextBox));
            thinkingTextFactory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
            thinkingTextFactory.SetValue(TextBox.IsReadOnlyProperty, true);
            thinkingTextFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
            thinkingTextFactory.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
            thinkingTextFactory.SetValue(TextBox.FontSizeProperty, 11.0);
            thinkingTextFactory.SetValue(TextBox.FontStyleProperty, FontStyles.Italic);
            thinkingTextFactory.SetValue(TextBox.ForegroundProperty, new SolidColorBrush(Color.FromRgb(140, 140, 140)));
            thinkingTextFactory.SetValue(TextBox.AcceptsReturnProperty, true);
            thinkingTextFactory.SetValue(TextBox.MaxHeightProperty, 200.0);
            thinkingTextFactory.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            thinkingTextFactory.SetValue(TextBox.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            thinkingTextFactory.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("ThinkingText"));

            expanderFactory.AppendChild(thinkingTextFactory);
            stackFactory.AppendChild(expanderFactory);

            // Main text
            var textFactory = new FrameworkElementFactory(typeof(TextBox));
            textFactory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
            textFactory.SetValue(TextBox.IsReadOnlyProperty, true);
            textFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
            textFactory.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
            textFactory.SetValue(TextBox.FontSizeProperty, 12.0);
            textFactory.SetValue(TextBox.AcceptsReturnProperty, true);
            textFactory.SetValue(TextBox.CursorProperty, Cursors.IBeam);
            textFactory.SetValue(TextBox.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            textFactory.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            textFactory.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("Text"));
            textFactory.SetBinding(TextBox.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToForegroundConverter() });

            stackFactory.AppendChild(textFactory);

            factory.AppendChild(stackFactory);

            template.VisualTree = factory;
            return template;
        }

        // ---- Send Message ----

        private void OnSend(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                // Act as Stop button
                try { _cts?.Cancel(); } catch { }
                return;
            }

            var text = _inputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Clear();
                _ai.StartNewSession();
                _inputBox.Clear();
                return;
            }

            _messages.Add(new ChatMessage("User", text));
            _inputBox.Clear();
            UpdateStatusInfo();

            _isProcessing = true;
            _sendBtn.Content = Lang.S("Stop");
            _sendBtn.Background = new SolidColorBrush(Color.FromRgb(160, 40, 40));
            _sendBtn.BorderBrush = Brushes.Transparent;
            _inputBox.Focus();

            _cts = new System.Threading.CancellationTokenSource();
            var token = _cts.Token;
            var capturedText = text;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _ai.Chat(capturedText, token);
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isProcessing = false;
                        _sendBtn.Content = Lang.S("Send");
                        _sendBtn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                        _sendBtn.BorderBrush = Brushes.Transparent;
                        _inputBox.Focus();
                        UpdateStatusInfo();
                        FlushPendingDelta();
                        if (_streamingMsg != null)
                        {
                            // Stream was interrupted or ended without OnAiTextEnd — flush
                            if (string.IsNullOrEmpty(_streamingMsg.Text))
                                _messages.Remove(_streamingMsg);
                            _streamingMsg = null;
                        }
                        try { _cts?.Dispose(); } catch { }
                        _cts = null;
                    }));
                }
            });
        }

        // 把累积的 _pendingDelta 一次性追加到 _streamingMsg.Text 并清空/停定时器。必须在 UI 线程调用。
        private void FlushPendingDelta()
        {
            if (_flushTimer != null) _flushTimer.Stop();
            if (!string.IsNullOrEmpty(_pendingDelta))
            {
                if (_streamingMsg != null)
                {
                    _streamingMsg.Text = (_streamingMsg.Text ?? "") + _pendingDelta;
                    _scrollViewer.ScrollToEnd();
                }
                _pendingDelta = null;
            }
        }

        // ---- Auto-save Session ----

        private void AutoSaveSession()
        {
            if (_messages.Count == 0) return;
            try
            {
                var saveData = _messages.Select(m =>
                {
                    var d = new Dictionary<string, string>
                    {
                        { "role", m.Role },
                        { "text", m.Text }
                    };
                    if (!string.IsNullOrEmpty(m.ThinkingText))
                        d["thinkingText"] = m.ThinkingText;
                    return d;
                }).ToList();
                _ai.SaveSession(_json.Serialize(saveData));
            }
            catch { }
        }

        // ---- About Dialog ----

        private void OnAbout(object sender, RoutedEventArgs e)
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "?";

            var win = new Window
            {
                Title = "关于 - AI助手",
                Width = 420,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                // 子对话框不应在任务栏留下独立按钮（RTSys 下 owner 可能为 null）
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = $"AI助手  v{verStr}",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var linkBlock = new TextBlock
            {
                Text = "github.com/lfmmd/RTSysAI",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 16)
            };
            linkBlock.MouseLeftButtonUp += (s, ev) =>
            {
                try { System.Diagnostics.Process.Start("https://github.com/lfmmd/RTSysAI"); } catch { }
            };
            panel.Children.Add(linkBlock);

            var giteeLink = new TextBlock
            {
                Text = "gitee.com/lfmmd/RTSysAI",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 16)
            };
            giteeLink.MouseLeftButtonUp += (s, ev) =>
            {
                try { System.Diagnostics.Process.Start("https://gitee.com/lfmmd/RTSysAI"); } catch { }
            };
            panel.Children.Add(giteeLink);

            panel.Children.Add(new TextBlock
            {
                Text = "免责声明",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "本插件由第三方开发（BY:LFMMD），与 RTSysAI 无关。\n\n插件按\"原样\"提供，不作任何明示或暗示的保证。使用本插件产生的任何风险由用户自行承担。",
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var okBtn = new Button
            {
                Content = "确定",
                Width = 70,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            okBtn.Click += (s, ev) => win.Close();
            panel.Children.Add(okBtn);

            ApplySafeOwner(win);
            win.Content = panel;
            win.ShowDialog();
        }

        // ---- Memory Dialog ----

        private void OnMemory(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = Lang.S("MemoryTitle"),
                Width = 500,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = Lang.S("MemoryDesc"),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var memoryBox = new TextBox
            {
                Text = AiService.GetMemoryText(),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 280,
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = new FontFamily("Consolas")
            };
            panel.Children.Add(memoryBox);

            var btnPanel = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };

            var clearBtn = new Button
            {
                Content = Lang.S("ClearMemory"),
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(160, 40, 40)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            clearBtn.Click += (s, ev) =>
            {
                AiService.ClearMemory();
                memoryBox.Text = "";
            };
            DockPanel.SetDock(clearBtn, Dock.Left);
            btnPanel.Children.Add(clearBtn);

            var btnRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(btnRight, Dock.Right);

            var cancelBtn = new Button
            {
                Content = Lang.S("Cancel"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
            };
            cancelBtn.Click += (s, ev) => win.Close();
            btnRight.Children.Add(cancelBtn);

            var saveBtn = new Button
            {
                Content = Lang.S("Save"),
                Width = 70, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            saveBtn.Click += (s, ev) =>
            {
                AiService.SaveMemory(memoryBox.Text);
                win.Close();
                _messages.Add(new ChatMessage("System", Lang.S("MemorySaved")));
                _scrollViewer.ScrollToEnd();
            };
            btnRight.Children.Add(saveBtn);

            btnPanel.Children.Add(btnRight);
            panel.Children.Add(btnPanel);

            ApplySafeOwner(win);
            win.Content = panel;
            win.ShowDialog();
        }

        // ---- Settings Dialog ----

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var win = new Window
            {
                Title = Lang.S("SettingsTitle"),
                Width = 450,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                // 关键：不显示任务栏按钮，否则关闭后任务栏残留可切换窗口
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            // RTSys controller IP（用于连接 ZMotion 控制器）
            panel.Children.Add(MakeLabel("控制器 IP (RTSys)"));
            var rtsysIpBox = new TextBox
            {
                Text = LoadConfigValue("RTSysIP"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "192.168.1.100"
            };
            panel.Children.Add(rtsysIpBox);
            panel.Children.Add(new TextBlock
            {
                Text = "用于连接 ZMotion 控制器，例如 192.168.1.100",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // API Key (masked)
            panel.Children.Add(MakeLabel(Lang.S("ApiKey")));
            var keyBox = new PasswordBox
            {
                Password = LoadConfigValue("apiKey"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0),
                PasswordChar = '*'
            };
            panel.Children.Add(keyBox);

            // API URL
            panel.Children.Add(MakeLabel(Lang.S("ApiUrl")));
            var urlBox = new TextBox
            {
                Text = LoadConfigValue("apiUrl"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(urlBox);

            // Model
            panel.Children.Add(MakeLabel(Lang.S("Model")));
            var modelBox = new TextBox
            {
                Text = LoadConfigValue("model"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(modelBox);

            // Light Model (subagents only; empty = fall back to main model)
            panel.Children.Add(MakeLabel(Lang.S("LightModel")));
            var lightModelBox = new TextBox
            {
                Text = LoadConfigValue("lightModel"),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(lightModelBox);

            // Show Tool Status checkbox
            var showStatusCheck = new CheckBox
            {
                Content = Lang.S("ShowToolStatus"),
                IsChecked = _ai.ShowToolStatus,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("ShowToolStatusDesc")
            };
            panel.Children.Add(showStatusCheck);

            // Include skill images checkbox
            var includeImagesCheck = new CheckBox
            {
                Content = Lang.S("IncludeSkillImages"),
                IsChecked = _ai.IncludeSkillImages,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("IncludeSkillImagesDesc")
            };
            panel.Children.Add(includeImagesCheck);

            // Controller validation checkbox
            var controllerValidationCheck = new CheckBox
            {
                Content = Lang.S("ControllerValidation"),
                IsChecked = AiService.EnableControllerValidation,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("ControllerValidationDesc")
            };
            panel.Children.Add(controllerValidationCheck);

            // Enable thinking checkbox
            var enableThinkingCheck = new CheckBox
            {
                Content = Lang.S("EnableThinking"),
                IsChecked = AiService.EnableThinking,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = Lang.S("EnableThinkingDesc")
            };
            panel.Children.Add(enableThinkingCheck);

            // Budget tokens input
            panel.Children.Add(MakeLabel(Lang.S("BudgetTokens")));
            var budgetBox = new TextBox
            {
                Text = AiService.BudgetTokens.ToString(),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Padding = new Thickness(6, 0, 6, 0)
            };
            panel.Children.Add(budgetBox);

            // Show thinking checkbox
            var showThinkingCheck = new CheckBox
            {
                Content = Lang.S("ShowThinking"),
                IsChecked = AiService.ShowThinking,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("ShowThinkingDesc")
            };
            panel.Children.Add(showThinkingCheck);

            // Memory enabled checkbox
            var memoryEnabledCheck = new CheckBox
            {
                Content = Lang.S("MemoryEnabled"),
                IsChecked = AiService.MemoryEnabled,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("MemoryEnabledDesc")
            };
            panel.Children.Add(memoryEnabledCheck);

            // Localize thinking checkbox
            var localizeThinkingCheck = new CheckBox
            {
                Content = Lang.S("LocalizeThinking"),
                IsChecked = AiService.LocalizeThinking,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("LocalizeThinkingDesc")
            };
            panel.Children.Add(localizeThinkingCheck);

            // Use Chinese documentation checkbox
            var useChineseDocsCheck = new CheckBox
            {
                Content = Lang.S("UseChineseDocs"),
                IsChecked = AiService.UseChineseDocs,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("UseChineseDocsDesc")
            };
            panel.Children.Add(useChineseDocsCheck);

            // Dialect mode: which prompt variant to use (Auto infers from project, else force TrioBASIC/IEC)
            var dialectLabel = new TextBlock
            {
                Text = Lang.S("DialectMode"),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(dialectLabel);
            var dialectBox = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 14),
                ToolTip = Lang.S("DialectModeDesc")
            };
            dialectBox.Items.Add(new ComboBoxItem { Content = Lang.S("DialectAuto"), Tag = "auto" });
            dialectBox.Items.Add(new ComboBoxItem { Content = "TrioBASIC", Tag = "triobasic" });
            dialectBox.Items.Add(new ComboBoxItem { Content = "IEC ST", Tag = "iec" });
            var curDialect = _ai.DialectMode ?? "auto";
            for (int i = 0; i < dialectBox.Items.Count; i++)
            {
                if (((ComboBoxItem)dialectBox.Items[i]).Tag as string == curDialect) { dialectBox.SelectedIndex = i; break; }
            }
            if (dialectBox.SelectedIndex < 0) dialectBox.SelectedIndex = 0;
            panel.Children.Add(dialectBox);

            // Buttons
            var btnRow = new DockPanel { LastChildFill = false };

            var initBtn = new Button
            {
                Content = "初始化 Skill 数据",
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(80, 50, 0)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 80, 0))
            };
            var skillStatus = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Text = _ai.SkillsInitialized ? "已释放" : "",
                Foreground = _ai.SkillsInitialized
                    ? new SolidColorBrush(Color.FromRgb(0, 200, 0))
                    : Brushes.Gray
            };
            initBtn.Click += (s, ev) =>
            {
                if (_ai.SkillsInitialized)
                {
                    var r = MessageBox.Show(win, "Skill 数据已释放，是否覆盖？", "确认覆盖",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
                var err = _ai.InitializeSkills();
                if (err != null)
                {
                    MessageBox.Show(win, err, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                MessageBox.Show(win, "Skill 数据初始化完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                skillStatus.Text = "已释放";
                skillStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 0));
            };
            DockPanel.SetDock(initBtn, Dock.Left);
            DockPanel.SetDock(skillStatus, Dock.Left);
            btnRow.Children.Add(initBtn);
            btnRow.Children.Add(skillStatus);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(btnPanel, Dock.Right);
            btnRow.Children.Add(btnPanel);

            var cancelBtn = new Button
            {
                Content = Lang.S("Cancel"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
            };
            cancelBtn.Click += (s, ev) => win.DialogResult = false;
            btnPanel.Children.Add(cancelBtn);

            var saveBtn = new Button
            {
                Content = Lang.S("Save"),
                Width = 70, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            saveBtn.Click += (s, ev) =>
            {
                var key = keyBox.Password.Trim();
                var url = urlBox.Text.Trim();
                var model = modelBox.Text.Trim();
                var lightModel = lightModelBox.Text.Trim();
                if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(url))
                {
                    MessageBox.Show(win, Lang.S("EmptyKey"), Lang.S("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int budget = AiService.BudgetTokens;
                int.TryParse(budgetBox.Text, out budget);
                _ai.SaveConfig(key, model, url, showStatusCheck.IsChecked, includeImagesCheck.IsChecked, controllerValidationCheck.IsChecked, enableThinkingCheck.IsChecked, budget, showThinkingCheck.IsChecked, memoryEnabledCheck.IsChecked, localizeThinkingCheck.IsChecked, useChineseDocsCheck.IsChecked, lightModel, ((ComboBoxItem)dialectBox.SelectedItem)?.Tag as string);
                var rtsysIp = rtsysIpBox.Text.Trim();
                if (rtsysIp.Length > 0 && !IsLikelyIPv4(rtsysIp))
                {
                    MessageBox.Show(win, "控制器 IP 格式看起来不像 IPv4，仍将按输入保存：" + rtsysIp,
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                SaveRTSysIp(rtsysIp);
                _messages.Add(new ChatMessage("System", Lang.S("SettingsSaved")));
                _messages.Add(new ChatMessage("System",
                    string.IsNullOrEmpty(rtsysIp) ? "控制器 IP 已清空" : "控制器 IP 已保存: " + rtsysIp));
                _scrollViewer.ScrollToEnd();
                win.DialogResult = true;
            };
            btnPanel.Children.Add(saveBtn);

            panel.Children.Add(btnRow);
            // 内容可能超出固定窗口高度（checkbox 多 + 高 DPI 缩放），用 ScrollViewer 兜底，
            // 保证底部按钮行（初始化/取消/保存）始终可达，未来再加设置项也无需改尺寸。
            ApplySafeOwner(win);
            win.Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            win.ShowDialog();
        }

        private void UpdateStatusInfo()
        {
            if (_statusInfo == null || _ai == null) return;
            var inTk = _ai.TotalInputTokens;
            var outTk = _ai.TotalOutputTokens;
            var cacheTk = _ai.TotalCacheReadTokens;
            var msgCount = _ai.HistoryMessageCount;
            if (inTk > 0)
            {
                var cacheInfo = cacheTk > 0 ? $" cache:{cacheTk / 1000}K" : "";
                _statusInfo.Text = $"In:{inTk / 1000}K Out:{outTk / 1000}K{cacheInfo} M:{msgCount}";
            }
            else
            {
                var tokens = _ai.HistoryTokenEstimate;
                _statusInfo.Text = $"~{tokens}K M:{msgCount}";
            }
        }

        // 子 agent 类型 → 本地化动作动词（驱动进度条 banner 文案，区分 research/review/debug/explore/verify）
        private static string SubagentVerb(string agentType)
        {
            switch (agentType)
            {
                case "review":  return Lang.L("审查", "reviewing");
                case "debug":   return Lang.L("诊断", "diagnosing");
                case "explore": return Lang.L("探索", "exploring");
                case "verify":  return Lang.L("验证", "verifying");
                default:        return Lang.L("调研", "investigating");   // research
            }
        }

        private static string UnescapeJsonDisplay(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // 先处理 JSON 转义序列
            s = s.Replace("\\r\\n", "\n")
                  .Replace("\\n", "\n")
                  .Replace("\\r", "")
                  .Replace("\\t", "  ")
                  .Replace("\\\"", "\"")
                  .Replace("\\\\", "\\");
            // 处理所有 \uXXXX 转义
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (i + 5 < s.Length && s[i] == '\\' && s[i + 1] == 'u')
                {
                    var hex = s.Substring(i + 2, 4);
                    ushort code;
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static TextBlock MakeLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static string LoadConfigValue(string key)
        {
            try
            {
                var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI", "config.json");
                if (!File.Exists(configPath))
                {
                    if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
                    if (key == "model") return "claude-sonnet-4-20250514";
                    return "";
                }
                var text = File.ReadAllText(configPath);
                var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                object val;
                if (cfg.TryGetValue(key, out val) && val != null && !string.IsNullOrEmpty(val.ToString()))
                    return val.ToString();
                // Return defaults for empty values
                if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
                if (key == "model") return "claude-sonnet-4-20250514";
            }
            catch { }
            if (key == "apiUrl") return "https://api.deepseek.com/anthropic";
            if (key == "model") return "claude-sonnet-4-20250514";
            return "";
        }

        /// <summary>
        /// 把 RTSysIP 写入 config.json：先读出全部现有键再只改 RTSysIP 后写回，
        /// 保证其它配置项（apiKey/apiUrl/model 等）不丢失。文件不存在则新建。
        /// </summary>
        private static void SaveRTSysIp(string ip)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RTSysAI");
                var configPath = Path.Combine(dir, "config.json");
                var cfg = new Dictionary<string, object>();
                if (File.Exists(configPath))
                {
                    var text = File.ReadAllText(configPath);
                    var loaded = _json.Deserialize<Dictionary<string, object>>(text);
                    if (loaded != null) cfg = loaded;
                }
                cfg["RTSysIP"] = ip ?? "";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(configPath, _json.Serialize(cfg));
                PlugInLog.Log("SaveRTSysIp() RTSysIP=" + (ip ?? ""));
            }
            catch (Exception ex)
            {
                try { PlugInLog.LogException("ChatPanel.SaveRTSysIp", ex); } catch { }
            }
        }

        /// <summary>粗略判断是否为 IPv4 点分十进制（仅用于提示，不阻断保存）。</summary>
        private static bool IsLikelyIPv4(string s)
        {
            var parts = s.Split('.');
            if (parts.Length != 4) return false;
            foreach (var p in parts)
            {
                int v;
                if (!int.TryParse(p, out v) || v < 0 || v > 255) return false;
            }
            return true;
        }

        // ---- History Dialog ----

        private void OnHistory(object sender, RoutedEventArgs e)
        {
            var sessions = _ai.ListSessions();
            if (sessions.Count == 0)
            {
                ShowMessage(Lang.S("NoHistory"), Lang.S("History"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new Window
            {
                Title = Lang.S("HistoryTitle"),
                Width = 420,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                ShowInTaskbar = false
            };

            var dock = new DockPanel();

            // Buttons at bottom
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            btnPanel.SetValue(DockPanel.DockProperty, Dock.Bottom);

            var deleteBtn = new Button
            {
                Content = Lang.S("Delete"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(160, 40, 40)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            btnPanel.Children.Add(deleteBtn);

            var openBtn = new Button
            {
                Content = Lang.S("Open"),
                Width = 70, Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent
            };
            btnPanel.Children.Add(openBtn);

            var closeBtn = new Button
            {
                Content = Lang.S("Close"),
                Width = 70, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
            };
            closeBtn.Click += (s, ev) => win.Close();
            btnPanel.Children.Add(closeBtn);

            dock.Children.Add(btnPanel);

            // Separator
            var sep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
            sep.SetValue(DockPanel.DockProperty, Dock.Bottom);
            dock.Children.Add(sep);

            // List
            var listBox = new ListBox
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(4)
            };

            foreach (var session in sessions)
            {
                var item = new ListBoxItem
                {
                    Tag = session.Id,
                    Content = $"{session.Time}  {session.Preview}"
                };
                item.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                item.FontSize = 12;
                item.Padding = new Thickness(4, 6, 4, 6);
                item.BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50));
                listBox.Items.Add(item);
            }

            openBtn.Click += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null)
                {
                    MessageBox.Show(win, Lang.S("SelectConversation"), Lang.S("History"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var sessionId = selected.Tag as string;
                LoadSessionMessages(sessionId);
                win.Close();
            };

            deleteBtn.Click += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null) return;
                var sessionId = selected.Tag as string;
                var confirm = MessageBox.Show(win, Lang.S("ConfirmDelete"), Lang.S("Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    _ai.DeleteSession(sessionId);
                    listBox.Items.Remove(selected);
                }
            };

            // Double-click to open
            listBox.MouseDoubleClick += (s, ev) =>
            {
                var selected = listBox.SelectedItem as ListBoxItem;
                if (selected == null) return;
                var sessionId = selected.Tag as string;
                LoadSessionMessages(sessionId);
                win.Close();
            };

            dock.Children.Add(listBox);
            ApplySafeOwner(win);
            win.Content = dock;
            win.ShowDialog();
        }

        private void LoadSessionMessages(string sessionId)
        {
            try
            {
                var text = _ai.LoadSession(sessionId);
                if (text == null) return;
                var data = _json.Deserialize<Dictionary<string, object>>(text);
                _messages.Clear();

                object msgsObj;
                if (data.TryGetValue("messages", out msgsObj) && msgsObj is System.Collections.ArrayList al)
                {
                    foreach (var m in al)
                    {
                        var md = m as Dictionary<string, object>;
                        if (md != null)
                        {
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "System";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            var thinking = md.ContainsKey("thinkingText") ? md["thinkingText"]?.ToString() : "";
                            var chatMsg = new ChatMessage(role, msg);
                            if (!string.IsNullOrEmpty(thinking)) chatMsg.ThinkingText = thinking;
                            _messages.Add(chatMsg);
                        }
                    }
                }
                _scrollViewer.ScrollToEnd();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Lang.S("Error")}: {ex.Message}", Lang.S("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---- Inline Confirmation ----

        private bool ShowInlineConfirmation(string toolName, string argsJson)
        {
            lock (_confirmLock)
            {
                var tcs = new TaskCompletionSource<bool>();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _messages.Add(new ChatMessage("System", $"{Lang.S("AIRequests")}: {toolName}\n{Truncate(UnescapeJsonDisplay(argsJson), 400)}"));
                    _scrollViewer.ScrollToEnd();

                    _inputPanel.Visibility = Visibility.Collapsed;
                    _confirmPanel.Visibility = Visibility.Visible;
                    _confirmTcs = tcs;
                }));

                return tcs.Task.Result;
            }
        }

        private void OnConfirmAllow(object sender, RoutedEventArgs e)
        {
            _confirmPanel.Visibility = Visibility.Collapsed;
            _inputPanel.Visibility = Visibility.Visible;
            _confirmTcs?.SetResult(true);
            _confirmTcs = null;
            _inputBox.Focus();
        }

        private void OnConfirmReject(object sender, RoutedEventArgs e)
        {
            _confirmPanel.Visibility = Visibility.Collapsed;
            _inputPanel.Visibility = Visibility.Visible;
            _messages.Add(new ChatMessage("System", Lang.S("Rejected")));
            _scrollViewer.ScrollToEnd();
            _confirmTcs?.SetResult(false);
            _confirmTcs = null;
            _inputBox.Focus();
        }

        public void AddMessage(string role, string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _messages.Add(new ChatMessage(role, text));
                _scrollViewer.ScrollToEnd();
            }));
        }

        // --- Tool<TControl> inner class ---
        private class ChatTool : Tool<ChatPanel>
        {
            public ChatTool(ChatPanel control)
                : base(control, (ToolFactory)(object)_factory)
            {
                Title = Lang.S("Title");
            }
        }

        // --- ToolFactory<TControl> inner class ---
        private class ChatPanelFactory : ToolFactory<ChatPanel>
        {
            public readonly string ToolName;

            public ChatPanelFactory(ToolPositionSettings defaultPosition)
                : base(defaultPosition, (KnownFunctionalities)0)
            {
                ToolName = CreateToolName();
                RegisterFunctionality(
                    ToolName,
                    "AI Assistant",
                    "AI-powered MotionPerfect assistant",
                    null,
                    FunctionalityPath.Tools);
            }

            protected override void OnApplyConfiguration(string key)
            {
                var functionalitySettings = GetFunctionalitySettings(key);
                if (functionalitySettings != null)
                {
                    InstallUIComponents(functionalitySettings.Item1, (IFunctionalityAccessibilityCheck)(object)functionalitySettings.Item2);
                }
            }

            protected override FunctionalityItem CreateDescription(string functionalityKey)
            {
                var desc = new FunctionalityItem();
                var mi = CreateMenuItem();
                FunctionalityConfigurationExtensions.AddUIComponent(
                    desc,
                    "MainWindow.Menu.Tools.AIAssistant",
                    null,
                    mi,
                    "MainWindow.Menu.Tools",
                    null,
                    true);
                return desc;
            }

            private MenuItem CreateMenuItem()
            {
                var mi = new MenuItem { Header = Lang.S("Title") };
                mi.Tag = (ToolFactory)(object)this;
                mi.Click += OnMenuItemClick;
                return mi;
            }

            private static void OnMenuItemClick(object sender, RoutedEventArgs e)
            {
                var mi = sender as MenuItem;
                if (mi?.Tag == null) return;
                var factory = mi.Tag as ToolFactory;
                if (factory == null) return;
                var mw = MPSingletons.MainWindow;
                if (mw == null) return;
                mw.OpenToolWindow(factory.CreateToolName(), true);
                e.Handled = true;
            }

            private void InstallUIComponents(IFunctionalityConfiguration fConfiguration, IFunctionalityAccessibilityCheck settings)
            {
                FunctionalityToolFactoryExtensions.AddUICreator(
                    (ToolFactoryBase)(object)this,
                    "MainWindow.Menu.Tools",
                    "MainWindow.Menu.Tools.AIAssistant",
                    (Func<object>)(() => CreateMenuItem()),
                    fConfiguration,
                    settings);
            }
        }
    }

    internal class ChatMessage : System.ComponentModel.INotifyPropertyChanged
    {
        private string _text;
        private string _thinkingText;
        public string Role { get; }
        public string Text
        {
            get { return _text; }
            set
            {
                if (_text != value)
                {
                    _text = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs("Text"));
                }
            }
        }
        public string ThinkingText
        {
            get { return _thinkingText; }
            set
            {
                if (_thinkingText != value)
                {
                    _thinkingText = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs("ThinkingText"));
                }
            }
        }
        public ChatMessage(string role, string text) { Role = role; _text = text; }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    #region Converters
    internal class RoleToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return new SolidColorBrush(Color.FromRgb(0, 100, 200));
            if (role == "System") return new SolidColorBrush(Color.FromRgb(60, 50, 20));
            if (role == "Subagent") return new SolidColorBrush(Color.FromRgb(20, 60, 110));   // 深蓝（与 research banner 同色系）
            return new SolidColorBrush(Color.FromRgb(55, 55, 55));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class RoleToAlignmentConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return HorizontalAlignment.Right;
            if (role == "System") return HorizontalAlignment.Center;
            return HorizontalAlignment.Left;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class RoleToForegroundConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "User") return Brushes.White;
            if (role == "System") return new SolidColorBrush(Color.FromRgb(255, 200, 50));
            return (Brush)new SolidColorBrush(Color.FromRgb(220, 220, 220));
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class RoleToHeaderConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var role = value as string;
            if (role == "Subagent") return Lang.L("📋 子 agent 轨迹", "📋 Subagent trace");
            return Lang.S("ThinkingLabel");
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    internal class ThinkingVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => null;
    }
    #endregion

    internal class SelectableTextBlock : TextBlock
    {
        public SelectableTextBlock()
        {
            ContextMenu = new ContextMenu();
            var copyItem = new MenuItem { Header = Lang.S("Copy") };
            copyItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(Text))
                    Clipboard.SetText(Text);
            };
            ContextMenu.Items.Add(copyItem);

            var copyAllItem = new MenuItem { Header = Lang.S("CopyAll") };
            copyAllItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(Text))
                    Clipboard.SetText(Text);
            };
            ContextMenu.Items.Add(copyAllItem);
        }
    }

}
