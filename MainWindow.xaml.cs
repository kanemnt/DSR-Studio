using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DSRStudio.Core;
using Microsoft.Win32;

namespace DSRStudio;

public partial class MainWindow : Window
{
    GameRoot _game = null!;
    string _domain = "text";
    readonly List<TabItem> _tabs = new();
    readonly List<Doc> _docs = new();

    Dictionary<string, ParamTable>? _paramTables;
    byte[]? _paramBuf;
    byte[]? _paramRaw;
    bool _paramDirty;
    static int _msbTabIndex = 0;
    bool _cliMode;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
        SideList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        };
        StateChanged += (_, _) =>
        {
            try { BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; } catch { }
        };
    }

    // ---- 自绘标题栏按钮 ----
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= 启动 =================

    void OnLoaded(object? s, RoutedEventArgs e)
    {
        var root = GameRoot.Locate();
        if (root == null)
        {
            var dlg = new OpenFolderDialog { Title = "选择 DARK SOULS REMASTERED 安装目录" };
            if (dlg.ShowDialog() != true) { Close(); return; }
            root = dlg.FolderName;
        }
        _game = new GameRoot(root);
        StatusRoot.Text = _game.Root;
        EmptyRoot.Text = _game.Root;

        var probe = _game.Full("DarkSoulsRemastered.exe");
        if (!File.Exists(probe))
        {
            MessageBox.Show(this, "该目录下找不到 DarkSoulsRemastered.exe。\n" + _game.Root,
                "目录不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ActText.IsChecked = true;
        LoadDomain("text");

        // --open <path> [<path>...]：启动即打开文件（便于脚本化截图/验证）
        var argv = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < argv.Length; i++)
        {
            bool cliArg = argv[i].StartsWith("--", StringComparison.Ordinal) && argv[i] != "--open";
            if (cliArg) _cliMode = true;
            if (argv[i] == "--tae" && i + 1 < argv.Length)
            {
                try
                {
                    var f = Tae.Parse(File.ReadAllBytes(argv[i + 1]));
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(f.Note + "  " + f.Size + " 字节  动画 " + f.Anims.Count);
                    foreach (var a in f.Anims)
                        sb.AppendLine($"  [{a.Index}] 动画ID={a.AnimId} 事件数={a.EventCount} 事件表@0x{a.EventTableOffset:X} 事件数组@0x{a.EventArrayOffset:X}");
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_tae.txt"), sb.ToString());
                }
                catch (Exception ex) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_tae.txt"), "异常: " + ex.Message); } catch { } }
                i += 1;
                continue;
            }
            if (argv[i] == "--quick" && i + 1 < argv.Length)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_quick.txt"), RunQuick(argv[i + 1])); }
                catch (Exception ex) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_quick.txt"), "异常: " + ex.Message); } catch { } }
                i += 1;
                continue;
            }
            if (argv[i] == "--flver-tree" && i + 1 < argv.Length)
            {
                try
                {
                    var rb = File.ReadAllBytes(argv[i + 1]);
                    var bnd = Dcx.Decompress(rb);
                    var fe = Bnd3.Entries(bnd).FirstOrDefault(x => x.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase));
                    var be = new BundleEntry { Name = fe.Name, Offset = fe.Offset, Size = fe.Size };
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_tree.txt"), Flver.Tree(rb, be));
                }
                catch (Exception ex) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsrstudio_tree.txt"), "异常: " + ex.Message); } catch { } }
                i += 1;
                continue;
            }
            if (argv[i] == "--bnd-replace" && i + 3 < argv.Length)
            {
                DoBndReplace(argv[i + 1], argv[i + 2], argv[i + 3]);
                i += 3;
                continue;
            }
            if (argv[i] == "--evd-instr" && i + 2 < argv.Length)
            {
                try
                {
                    var f = Emevd.Parse(File.ReadAllBytes(argv[i + 1]));
                    if (int.TryParse(argv[i + 2], out int ei) && ei >= 0 && ei < f.Events.Count)
                        ShowInstrTab(Path.GetFileName(argv[i + 1]), f.Raw, f.Events[ei]);
                }
                catch { }
                i += 2;
                continue;
            }
            if (argv[i] == "--msb-tab" && i + 1 < argv.Length)
            {
                if (int.TryParse(argv[i + 1], out int ti)) _msbTabIndex = ti;
                i += 1;
                continue;
            }
            if (argv[i] == "--msb-point-set" && i + 4 < argv.Length)
            {
                DoMsbPointSet(argv[i + 1], argv[i + 2], argv[i + 3], argv[i + 4]);
                i += 4;
                continue;
            }
            if (argv[i] == "--msb-set" && i + 4 < argv.Length)
            {
                DoMsbSet(argv[i + 1], argv[i + 2], argv[i + 3], argv[i + 4]);
                i += 4;
                continue;
            }
            if (argv[i] == "--tex-export" && i + 3 < argv.Length)
            {
                DoTexExport(argv[i + 1], argv[i + 2], argv[i + 3]);
                i += 3;
                continue;
            }
            if (argv[i] == "--tex-replace" && i + 3 < argv.Length)
            {
                DoTexReplace(argv[i + 1], argv[i + 2], argv[i + 3]);
                i += 3;
                continue;
            }
            if (argv[i] == "--param-set" && i + 4 < argv.Length)
            {
                DoParamSet(argv[i + 1], argv[i + 2], argv[i + 3], argv[i + 4]);
                i += 4;
                continue;
            }
            if (argv[i] == "--param")
            {
                try { _paramTables = null; LoadParamBundle(); OpenParamDoc(argv[i + 1]); } catch { }
                continue;
            }
            if (argv[i] != "--open") continue;
            var path = argv[i + 1];
            if (!File.Exists(path)) continue;
            if (path.EndsWith(".tpf.dcx", StringComparison.OrdinalIgnoreCase)) OpenTextureDoc(path);
            else if (path.EndsWith(".msgbnd.dcx", StringComparison.OrdinalIgnoreCase)) OpenTextDoc(path);
            else if (path.EndsWith(".msb", StringComparison.OrdinalIgnoreCase)) OpenMsbDoc(path);
            else if (path.EndsWith(".emevd.dcx", StringComparison.OrdinalIgnoreCase)) OpenEmevdDoc(path);
            else if (path.EndsWith(".anibnd.dcx", StringComparison.OrdinalIgnoreCase)) OpenTaeDoc(path);
            else if (path.EndsWith(".chrbnd.dcx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".partsbnd.dcx", StringComparison.OrdinalIgnoreCase)) OpenBundleDoc(path);
        }

        if (_cliMode)
        {
            Application.Current.Shutdown(0);
            return;
        }
    }

    void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveDoc(); e.Handled = true; }
        else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) { CloseCurrentTab(); e.Handled = true; }
    }

    // ================= 活动栏 / 侧栏 =================

    void Activity_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string d && IsLoaded) LoadDomain(d);
    }

    sealed class Item
    {
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Kind { get; init; } = "";
    }

    void LoadDomain(string domain)
    {
        if (_game == null) return;
        _domain = domain;
        var items = new List<Item>();

        switch (domain)
        {
            case "text":
                SideTitle.Text = "文本 / FMG";
                foreach (var f in _game.MsgBundles().OrderByDescending(x => x.Contains("SCHINESE")).ThenBy(x => x))
                    items.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "msgbnd", Subtitle = Rel(f) });
                SideHint.Text = "文本（FMG）：游戏内所有显示字符串，按语言分目录。双击 msgbnd 打开，左侧选表，右侧改文本（等长或更短）。";
                StatusDomain.Text = "文本";
                break;

            case "texture":
                SideTitle.Text = "纹理 / TPF";
                foreach (var f in _game.TpfFiles().OrderBy(x => x))
                    items.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "tpf", Subtitle = Rel(f) });
                SideHint.Text = "纹理（TPF）：界面/角色/地图贴图包。双击打开可预览、导出 DDS、替换回写（需保持尺寸与格式）。";
                StatusDomain.Text = "纹理";
                break;

            case "param":
                SideTitle.Text = "参数 / PARAM";
                try
                {
                    LoadParamBundle();
                    foreach (var kv in _paramTables!.OrderBy(k => k.Key))
                        items.Add(new Item
                        {
                            Path = "param:" + kv.Key,
                            Title = FriendlyTable(kv.Key),
                            Kind = "ptable",
                            Subtitle = $"{kv.Value.Rows.Count} 行 · 行宽 {kv.Value.RowStride} · {(kv.Value.Def?.Fields.Count ?? 0)} 字段"
                        });
                }
                catch (Exception ex)
                {
                    items.Add(new Item { Path = "param:error", Title = "参数加载失败", Subtitle = ex.Message });
                }
                SideHint.Text = "参数（PARAM）：数值表，41 张。双击表名打开，可直接改单元格后 Ctrl+S 写回（自动快照 + 回读校验）。";
                StatusDomain.Text = "参数";
                break;

            case "map":
                SideTitle.Text = "地图 / MSB";
                foreach (var f in _game.MsbFiles().OrderBy(x => x))
                    items.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "msb", Subtitle = Rel(f) });
                SideHint.Text = "地图（MSB）：模型 / 事件 / 区域 / 部件四张表，只读。碰撞（Havok hkx）不在修改范围内。";
                StatusDomain.Text = "地图";
                break;

            case "event":
                SideTitle.Text = "事件 / EMEVD";
                foreach (var f in _game.EmevdFiles().OrderBy(x => x))
                    items.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "emevd", Subtitle = Rel(f) });
                SideHint.Text = "事件（EMEVD）：地图脚本。双击打开可看事件表（ID / 指令数 / 偏移）；指令反汇编尚未接入。";
                StatusDomain.Text = "事件";
                break;

            case "model":
                SideTitle.Text = "模型 / FLVER";
                foreach (var f in _game.ChrbndFiles().OrderBy(x => x).Take(400))
                    items.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "chrbnd", Subtitle = Rel(f) });
                SideHint.Text = "模型（FLVER）：角色/部件模型容器。双击打开可看容器内条目与 FLVER 头部概要；骨骼/材质未解析。";
                StatusDomain.Text = "模型";
                break;

            case "backup":
                SideTitle.Text = "备份与回滚";
                items.AddRange(SnapshotItems());
                SideHint.Text = "双击一条快照即可把它写回原路径。";
                StatusDomain.Text = "备份";
                break;
        }

        SideList.ItemsSource = items;
        SideCount.Text = items.Count + " 项";
        StatusInfo.Text = $"{items.Count} 项";
    }

    string Rel(string full) => full.StartsWith(_game.Root, StringComparison.OrdinalIgnoreCase)
        ? full[(_game.Root.Length + 1)..] : full;

    List<Item> SnapshotItems()
    {
        var list = new List<Item>();
        var dir = Path.Combine(_game.Root, "_ModDev", "snapshots");
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTime))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
            list.Add(new Item { Path = f, Title = Path.GetFileName(f), Kind = "snapshot", Subtitle = parent });
        }
        return list;
    }

    void SideList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    void OpenSelected()
    {
        if (SideList.SelectedItem is not Item it) return;
        switch (it.Kind)
        {
            case "msgbnd": OpenTextDoc(it.Path); break;
            case "tpf": OpenTextureDoc(it.Path); break;
            case "ptable":
                if (it.Path.StartsWith("param:")) OpenParamDoc(it.Path[6..]);
                break;
            case "msb": OpenMsbDoc(it.Path); break;
            case "emevd": OpenEmevdDoc(it.Path); break;
            case "chrbnd": OpenBundleDoc(it.Path); break;
            case "snapshot": RestoreSnapshot(it.Path); break;
            default: OpenInfoDoc(it); break;
        }
    }

    // ================= 标签页 =================

    sealed class Doc
    {
        public string Path = "";
        public byte[] Raw = Array.Empty<byte>();
        public byte[] Buffer = Array.Empty<byte>();
        public bool Dirty;
        public object? State;
    }

    Doc? CurrentDoc()
    {
        int i = Tabs.SelectedIndex;
        return i >= 0 && i < _docs.Count ? _docs[i] : null;
    }

    TabItem AddTab(string title, UIElement content, Doc doc)
    {
        TabItem? tab = null;
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });

        var close = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 18,
            Height = 18,
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("TextDim"),
            ToolTip = "关闭"
        };
        close.SetResourceReference(StyleProperty, "VsBtn");
        close.Click += (_, _) => CloseTabAt(tab);

        header.Children.Add(close);

        tab = new TabItem { Header = header, Content = content };
        Tabs.Items.Add(tab);
        _tabs.Add(tab);
        _docs.Add(doc);
        Tabs.SelectedItem = tab;
        EmptyHint.Visibility = Visibility.Collapsed;
        return tab!;
    }

    void CloseCurrentTab() => CloseTabAt(Tabs.SelectedItem as TabItem);

    void CloseTabAt(TabItem? tab)
    {
        if (tab == null) return;
        int i = _tabs.IndexOf(tab);
        if (i < 0) return;
        _tabs.RemoveAt(i);
        _docs.RemoveAt(i);
        Tabs.Items.Remove(tab);
        StatusDirty.Text = _docs.Any(d => d.Dirty) ? "● 未保存" : "";
        if (Tabs.Items.Count == 0) EmptyHint.Visibility = Visibility.Visible;
    }

    void MarkDirty(Doc doc, bool dirty)
    {
        doc.Dirty = dirty;
        StatusDirty.Text = dirty ? "● 未保存" : "";
    }

    /// <summary>统一的“这一段是什么”说明条。</summary>
    TextBlock MakeNote(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap
        };
        tb.SetResourceReference(TextBlock.StyleProperty, "DimText");
        return tb;
    }

    // ================= 文本域 =================

    sealed class TextRow
    {
        public uint Id { get; init; }
        public int OldLen { get; init; }
        public string Text { get; set; } = "";
    }

    sealed class TextState
    {
        public bool HideEmpty = true;
        public List<FmgEntry> Entries = new();
        public ObservableCollection<TextRow> Rows = new();
        public int FmgOff, FmgSize;
        public string FmgName = "";
        public List<(string Name, int Off, int Size)> Fmgs = new();
        public DataGrid Grid = null!;
    }

    void OpenTextDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var raw = File.ReadAllBytes(path);
            var buf = Dcx.Decompress(raw);
            var ent = Bnd3.Entries(buf);
            if (ent.Count == 0)
            {
                ShowErrorTab(Path.GetFileName(path), "未解析到内部条目");
                return;
            }

            var doc = new Doc { Path = path, Raw = raw, Buffer = buf };
            var st = new TextState { Fmgs = ent.Select(x => (x.Name, x.Offset, x.Size)).ToList() };
            doc.State = st;

            // 左：FMG 列表（常驻，不弹层）
            var list = new ListBox
            {
                Background = (Brush)FindResource("SidebarBg"),
                BorderThickness = new Thickness(0),
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "字符串表（每个 .fmg）= 一类文本，例如 Item_name 是物品名、Weapon_desc 是武器说明、Conversation 是 NPC 对话。"
            };
            list.SetResourceReference(ListBox.ItemContainerStyleProperty, "SideItem");
            foreach (var f in st.Fmgs)
                list.Items.Add(new ListBoxItem { Content = f.Name, ToolTip = f.Name });

            var info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            info.SetResourceReference(TextBlock.StyleProperty, "DimText");

            var btnSave = new Button { Content = "保存 (Ctrl+S)", Margin = new Thickness(10, 0, 0, 0) };
            btnSave.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnSave.Click += (_, _) => SaveDoc();

            var btnRevert = new Button { Content = "回滚文件", Margin = new Thickness(6, 0, 0, 0) };
            btnRevert.SetResourceReference(StyleProperty, "VsBtn");
            btnRevert.Click += (_, _) => RollbackDoc(doc);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            var fmgTitle = new TextBlock { Text = "FMG", VerticalAlignment = VerticalAlignment.Center };
            fmgTitle.SetResourceReference(TextBlock.StyleProperty, "UiText");
            bar.Children.Add(fmgTitle);

            var chkHide = new CheckBox { Content = "隐藏空条目", IsChecked = true, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chkHide.SetResourceReference(StyleProperty, "CheckModern");
            chkHide.Click += (_, _) =>
            {
                st.HideEmpty = chkHide.IsChecked == true;
                if (list.SelectedIndex >= 0) LoadFmg(doc, st, list.SelectedIndex, info);
            };
            bar.Children.Add(chkHide);
            bar.Children.Add(info);
            bar.Children.Add(btnSave);
            bar.Children.Add(btnRevert);

            var grid = new DataGrid { IsReadOnly = false };
            grid.SetResourceReference(StyleProperty, "VsGrid");
            grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(TextRow.Id)), Width = 90, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "原长", Binding = new Binding(nameof(TextRow.OldLen)), Width = 60, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "文本", Binding = new Binding(nameof(TextRow.Text)) { Mode = BindingMode.TwoWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            st.Grid = grid;

            grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => CommitEdits(doc, st)),
                System.Windows.Threading.DispatcherPriority.Background);

            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedIndex < 0) return;
                LoadFmg(doc, st, list.SelectedIndex, info);
            };

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(list, 0);
            body.Children.Add(list);
            Grid.SetColumn(grid, 1);
            body.Children.Add(grid);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new StackPanel();
            head.Children.Add(bar);
            head.Children.Add(MakeNote(
                "这是游戏里的显示文本（FMG）。左侧是该 msgbnd 内的各张字符串表（物品名、武器名、对话、提示等），" +
                "右侧是表内条目：ID = 程序引用的编号，原长 = 原文字数（改短会补零，改长会被拒绝），文本 = 实际显示内容。" +
                "写回时自动快照，可随时回滚。"));

            var top = new Border
            {
                Child = head,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            AddTab(Path.GetFileName(path), root, doc);
            list.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    void LoadFmg(Doc doc, TextState st, int index, TextBlock info)
    {
        var (name, off, size) = st.Fmgs[index];
        st.FmgName = name; st.FmgOff = off; st.FmgSize = size;
        st.Entries = Fmg.Parse(doc.Buffer, off, size);
        st.Rows.Clear();
        int empty = 0;
        foreach (var e in st.Entries)
        {
            if (e.Text.Length == 0)
            {
                empty++;
                if (st.HideEmpty) continue;
            }
            st.Rows.Add(new TextRow { Id = e.Id, OldLen = e.ByteLength / 2, Text = e.Text });
        }
        st.Grid.ItemsSource = st.Rows;
        info.Text = empty > 0
            ? $"{name} · 共 {st.Entries.Count} 条，其中 {empty} 条为空（游戏原始数据即为空）· 只支持等长或更短替换"
            : $"{name} · {st.Entries.Count} 条 · 只支持等长或更短替换";
    }

    void CommitEdits(Doc doc, TextState st)
    {
        bool changed = false;
        foreach (var row in st.Rows)
        {
            var entry = st.Entries.FirstOrDefault(x => x.Id == row.Id);
            if (entry == null || entry.Text == row.Text) continue;
            if (!Fmg.Replace(doc.Buffer, entry, row.Text, out _))
            {
                MessageBox.Show(this, $"ID {row.Id}：新文本过长，已还原。", "长度限制",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                row.Text = entry.Text;
                st.Grid.Items.Refresh();
                continue;
            }
            entry.Text = row.Text;
            changed = true;
        }
        if (changed) MarkDirty(doc, true);
    }

    // ================= 纹理域 =================

    sealed class TexState
    {
        public List<TpfTexture> Textures = new();
        public Image Preview = null!;
        public TextBlock Info = null!;
    }

    void OpenTextureDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var raw = File.ReadAllBytes(path);
            var buf = Dcx.Decompress(raw);
            var texs = Tpf.Textures(buf, raw);
            if (texs.Count == 0)
            {
                ShowErrorTab(Path.GetFileName(path),
                    $"未解析到可识别的 DDS 纹理\n容器解包后 {buf.Length:N0} 字节，原始 {raw.Length:N0} 字节");
                return;
            }

            var doc = new Doc { Path = path, Raw = raw, Buffer = buf };
            var st = new TexState { Textures = texs };
            doc.State = st;

            var list = new ListBox { Background = (Brush)FindResource("SidebarBg"), BorderThickness = new Thickness(0) };
            list.SetResourceReference(ListBox.ItemContainerStyleProperty, "SideItem");
            foreach (var t in texs)
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{t.SizeText}  ·  {t.Format}",
                    Padding = new Thickness(10, 5, 10, 5)
                });

            st.Preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
            var canvas = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                Child = st.Preview
            };
            st.Info = new TextBlock { Margin = new Thickness(12, 6, 0, 10), VerticalAlignment = VerticalAlignment.Center };
            st.Info.SetResourceReference(TextBlock.StyleProperty, "DimText");

            var btnExportDds = new Button { Content = "导出 DDS", Margin = new Thickness(10, 6, 0, 10), Padding = new Thickness(10, 3, 10, 3) };
            btnExportDds.SetResourceReference(StyleProperty, "VsBtn");
            btnExportDds.Click += (_, _) =>
            {
                int idx = list.SelectedIndex;
                if (idx < 0) return;
                var t = st.Textures[idx];
                int block = Tpf.BlockBytesOf(t.FourCC, t.Dxgi);
                bool raw = block == 0;
                int chain = Tpf.ChainSize(t.Width, t.Height, block, raw);
                if (chain <= 0 || t.DataOffset + chain > buf.Length) { StatusInfo.Text = "该纹理数据不完整，无法导出"; return; }
                var data = new byte[chain];
                Buffer.BlockCopy(buf, t.DataOffset, data, 0, chain);
                var dds = Tpf.BuildDds(t.Width, t.Height, t.FourCC, t.Dxgi, data);

                var dlg = new SaveFileDialog
                {
                    Filter = "DDS 贴图|*.dds",
                    FileName = $"{Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))}_{idx}_{t.SizeText}.dds"
                };
                if (dlg.ShowDialog() != true) return;
                File.WriteAllBytes(dlg.FileName, dds);
                StatusInfo.Text = $"已导出 {dlg.FileName}（{t.SizeText} · {t.Format} · {chain:N0} 字节）";
            };

            var btnReplace = new Button { Content = "替换贴图…", Margin = new Thickness(8, 6, 0, 10), Padding = new Thickness(10, 3, 10, 3) };
            btnReplace.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnReplace.Click += (_, _) =>
            {
                int idx = list.SelectedIndex;
                if (idx < 0) { StatusInfo.Text = "先选中一张纹理"; return; }
                var t = st.Textures[idx];

                var dlg = new OpenFileDialog { Filter = "DDS 贴图|*.dds" };
                if (dlg.ShowDialog() != true) return;

                byte[] dds;
                try { dds = File.ReadAllBytes(dlg.FileName); }
                catch (Exception ex) { StatusInfo.Text = "读取失败：" + ex.Message; return; }

                var info = Tpf.ParseDds(dds);
                if (info == null) { StatusInfo.Text = "不是有效的 DDS 文件"; return; }
                if (info.Width != t.Width || info.Height != t.Height)
                {
                    StatusInfo.Text = $"尺寸不符：目标 {t.Width}×{t.Height}，导入 {info.Width}×{info.Height}";
                    return;
                }

                int block = Tpf.BlockBytesOf(t.FourCC, t.Dxgi);
                bool raw = block == 0;
                int chain = Tpf.ChainSize(t.Width, t.Height, block, raw);
                if (chain <= 0 || t.DataOffset + chain > buf.Length) { StatusInfo.Text = "目标纹理数据不完整"; return; }

                if (info.DataLength < chain)
                {
                    StatusInfo.Text = $"导入数据不足：需要 {chain:N0} 字节（含 mip 链），实际 {info.DataLength:N0}" +
                                      "。请导出后在同尺寸/同格式下编辑，或保存为包含 mip 的 DDS。";
                    return;
                }

                try
                {
                    var old = new byte[chain];
                    Buffer.BlockCopy(buf, t.DataOffset, old, 0, chain);

                    Buffer.BlockCopy(dds, info.DataOffset, buf, t.DataOffset, chain);
                    var written = Dcx.Compress(buf, doc.Raw);
                    var snap = Snapshot(path);
                    File.WriteAllBytes(path, written);

                    // 回读校验：确认像素区已写入且其它区域未变
                    var verify = Dcx.Decompress(File.ReadAllBytes(path));
                    bool same = verify.Length == buf.Length;
                    int diffOutside = 0;
                    if (same)
                    {
                        for (int i = 0; i < buf.Length; i++)
                        {
                            if (i >= t.DataOffset && i < t.DataOffset + chain) continue;
                            if (verify[i] != buf[i]) { diffOutside++; if (diffOutside > 4) break; }
                        }
                    }
                    if (!same || diffOutside > 0)
                    {
                        File.Copy(snap, path, true);
                        StatusInfo.Text = "写入校验失败，已回滚";
                        return;
                    }

                    doc.Raw = written;
                    list.Items.Refresh();
                    ShowTexture(buf, st, idx);
                    StatusInfo.Text = $"已替换 {t.SizeText} 贴图 · {Path.GetFileName(dlg.FileName)} · 快照 {Path.GetFileName(snap)}";
                }
                catch (Exception ex)
                {
                    StatusInfo.Text = "替换失败：" + ex.Message;
                }
            };

            var btnExport = new Button { Content = "导出 PNG", Margin = new Thickness(8, 6, 12, 10), Padding = new Thickness(10, 3, 10, 3) };
            btnExport.SetResourceReference(StyleProperty, "VsBtn");
            btnExport.Click += (_, _) =>
            {
                if (st.Preview.Source is not BitmapSource bs) { StatusInfo.Text = "当前纹理没有可导出的图像"; return; }
                var dlg = new SaveFileDialog
                {
                    Filter = "PNG 图像|*.png",
                    FileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)) + "_tex.png"
                };
                if (dlg.ShowDialog() != true) return;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                using var fs = File.Create(dlg.FileName);
                enc.Save(fs);
                StatusInfo.Text = "已导出 " + dlg.FileName;
            };

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var texNote = MakeNote(
                "这是贴图包（TPF）里的纹理。左侧列表显示尺寸与压缩格式（BC1/BC3/BC7…），" +
                "右侧是解码预览。「导出 DDS」拿到完整 mip 链的原始数据，改完用「替换贴图…」写回——" +
                "必须保持尺寸与格式不变，否则会花屏。");
            Grid.SetRow(texNote, 0);
            right.Children.Add(texNote);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(canvas, 1);
            right.Children.Add(canvas);
            var infoRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(btnExport, Dock.Right);
            infoRow.Children.Add(btnExport);
            DockPanel.SetDock(btnReplace, Dock.Right);
            infoRow.Children.Add(btnReplace);
            DockPanel.SetDock(btnExportDds, Dock.Right);
            infoRow.Children.Add(btnExportDds);
            infoRow.Children.Add(st.Info);
            Grid.SetRow(infoRow, 2);
            right.Children.Add(infoRow);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(list, 0);
            root.Children.Add(list);
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            list.SelectionChanged += (_, _) =>
            {
                int i = list.SelectedIndex;
                if (i < 0) return;
                ShowTexture(buf, st, i);
            };

            AddTab(Path.GetFileName(path), root, doc);
            list.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    void ShowErrorTab(string title, string message)
    {
        var doc = new Doc { Path = "error:" + title };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var head = new TextBlock { Text = "打开失败：" + title, FontSize = 15 };
        head.SetResourceReference(TextBlock.StyleProperty, "UiText");
        head.Foreground = (Brush)FindResource("ErrFg");
        panel.Children.Add(head);

        var body = new TextBlock { Text = message, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        body.SetResourceReference(TextBlock.StyleProperty, "MonoText");
        body.Foreground = (Brush)FindResource("TextDim");
        panel.Children.Add(body);

        AddTab(title + "（错误）", panel, doc);
        StatusInfo.Text = "打开失败：" + message;
    }

    void ShowTexture(byte[] buf, TexState st, int index)
    {
        var t = st.Textures[index];
        var px = Tpf.Decode(buf, t);
        if (px == null)
        {
            st.Preview.Source = null;
            st.Info.Text = $"{t.SizeText} · {t.Format} · 预览失败：{Tpf.LastError}";
            return;
        }
        var bmp = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Bgra32, null, px, t.Width * 4);
        bmp.Freeze();
        st.Preview.Source = bmp;
        st.Info.Text = $"{t.SizeText} · {t.Format} · {t.MipSize:N0} 字节 / 首级 mip";
    }

    // ---------- 地图命令行（验证用） ----------

    void DoMsbSet(string msbPath, string indexText, string axis, string valueText)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "dsrstudio_msb.txt");
        try
        {
            if (!int.TryParse(indexText, out int index)) { File.WriteAllText(logPath, "index 无效"); return; }
            if (!float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
            { File.WriteAllText(logPath, "数值无效"); return; }

            var data = File.ReadAllBytes(msbPath);
            var sections = Msb.Sections(data);
            var partSec = sections.FirstOrDefault(s => s.Name == "PARTS_PARAM_ST");
            if (partSec == null || index < 0 || index >= partSec.Records.Count)
            { File.WriteAllText(logPath, "部件索引越界"); return; }

            int off = axis.ToLowerInvariant() switch
            {
                "x" => Msb.PartX,
                "y" => Msb.PartY,
                "z" => Msb.PartZ,
                _ => -1
            };
            if (off < 0) { File.WriteAllText(logPath, "轴必须是 x/y/z"); return; }

            int rec = partSec.Records[index];
            float before = Msb.ReadFloat(data, rec, off);
            Msb.WriteFloat(data, rec, off, value);

            var snapDir = Path.Combine(Path.GetDirectoryName(msbPath)!, "_snapshot");
            var snap = Path.Combine(snapDir, Path.GetFileName(msbPath) + "." + DateTime.Now.ToString("HHmmss") + ".bak");
            Directory.CreateDirectory(snapDir);
            File.Copy(msbPath, snap, true);

            File.WriteAllBytes(msbPath, data);

            var check = File.ReadAllBytes(msbPath);
            float after = Msb.ReadFloat(check, rec, off);
            File.WriteAllText(logPath,
                $"部件[{index}] @0x{rec:X} {axis.ToUpperInvariant()}: {before:0.###} → {after:0.###}\n" +
                $"快照={Path.GetFileName(snap)} 大小={check.Length} 校验={(Math.Abs(after - value) < 1e-6 ? "通过" : "失败")}");
        }
        catch (Exception ex) { try { File.WriteAllText(logPath, "异常: " + ex.Message); } catch { } }
    }

    void DoMsbPointSet(string msbPath, string indexText, string axis, string valueText)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "dsrstudio_msb.txt");
        try
        {
            if (!int.TryParse(indexText, out int index)) { File.WriteAllText(logPath, "index 无效"); return; }
            if (!float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
            { File.WriteAllText(logPath, "数值无效"); return; }

            var data = File.ReadAllBytes(msbPath);
            var sections = Msb.Sections(data);
            var sec = sections.FirstOrDefault(s => s.Name == "POINT_PARAM_ST");
            if (sec == null || index < 0 || index >= sec.Records.Count)
            { File.WriteAllText(logPath, "区域索引越界"); return; }

            int off = axis.ToLowerInvariant() switch
            {
                "x" => Msb.PointX,
                "y" => Msb.PointY,
                "z" => Msb.PointZ,
                _ => -1
            };
            if (off < 0) { File.WriteAllText(logPath, "轴必须是 x/y/z"); return; }

            int rec = sec.Records[index];
            float before = Msb.ReadFloat(data, rec, off);
            Msb.WriteFloat(data, rec, off, value);

            var snapDir = Path.Combine(Path.GetDirectoryName(msbPath)!, "_snapshot");
            Directory.CreateDirectory(snapDir);
            var snap = Path.Combine(snapDir, Path.GetFileName(msbPath) + "." + DateTime.Now.ToString("HHmmss") + ".bak");
            File.Copy(msbPath, snap, true);

            File.WriteAllBytes(msbPath, data);

            var check = File.ReadAllBytes(msbPath);
            float after = Msb.ReadFloat(check, rec, off);
            File.WriteAllText(logPath,
                $"区域[{index}] @0x{rec:X} {axis.ToUpperInvariant()}: {before:0.###} → {after:0.###}\n" +
                $"快照={Path.GetFileName(snap)} 大小={check.Length} 校验={(Math.Abs(after - value) < 1e-6 ? "通过" : "失败")}");
        }
        catch (Exception ex) { try { File.WriteAllText(logPath, "异常: " + ex.Message); } catch { } }
    }

    // ================= 地图域（可改坐标） =================

    sealed class MsbRow
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public uint Type { get; init; }
        public string Sib { get; init; } = "";
        public string OffsetText { get; init; } = "";
    }

    void OpenMsbDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var data = File.ReadAllBytes(path);
            var sections = Msb.Sections(data);
            if (sections.Count == 0)
            {
                ShowErrorTab(Path.GetFileName(path), "未识别到 MSB 段（MODEL/EVENT/POINT/PARTS）");
                return;
            }

            var doc = new Doc { Path = path, Raw = data, Buffer = data };
            var buf = doc.Buffer;
            var modelSec = sections.FirstOrDefault(s => s.Name == "MODEL_PARAM_ST");
            var eventSec = sections.FirstOrDefault(s => s.Name == "EVENT_PARAM_ST");
            var pointSec = sections.FirstOrDefault(s => s.Name == "POINT_PARAM_ST");
            var partSec = sections.FirstOrDefault(s => s.Name == "PARTS_PARAM_ST");

            DataGrid MakeGrid()
            {
                var g = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false };
                g.SetResourceReference(StyleProperty, "VsGrid");
                return g;
            }
            DataGridTextColumn Col(string header, string binding, double width, bool star = false) =>
                new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(binding),
                    Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : width
                };

            // 版本：表格 + 顶部说明
            TabItem MakeTab(string header, string desc, DataGrid grid)
            {
                var note = new TextBlock
                {
                    Text = desc,
                    Margin = new Thickness(12, 8, 12, 8),
                    TextWrapping = TextWrapping.Wrap
                };
                note.SetResourceReference(TextBlock.StyleProperty, "DimText");

                var host = new Grid();
                host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(note, 0);
                host.Children.Add(note);
                Grid.SetRow(grid, 1);
                host.Children.Add(grid);

                return new TabItem { Header = header, Content = host };
            }

            // 模型
            var gModels = MakeGrid();
            gModels.Columns.Add(Col("#", nameof(MsbModel.Index), 60));
            gModels.Columns.Add(Col("模型名", nameof(MsbModel.Name), 150));
            gModels.Columns.Add(Col("类型", nameof(MsbModel.Type), 60));
            gModels.Columns.Add(Col("sib 路径", nameof(MsbModel.SibPath), 0, true));
            var models = modelSec == null ? new List<MsbModel>() : Msb.Models(data, modelSec);
            gModels.ItemsSource = models;

            // 事件
            var gEvents = MakeGrid();
            gEvents.Columns.Add(Col("#", nameof(MsbEvent.Index), 60));
            gEvents.Columns.Add(Col("事件名", nameof(MsbEvent.Name), 260));
            gEvents.Columns.Add(Col("序号", nameof(MsbEvent.Seq), 70));
            gEvents.Columns.Add(Col("标志（推测）", nameof(MsbEvent.Field1), 110));
            gEvents.Columns.Add(Col("参数块偏移", nameof(MsbEvent.ParamOffset), 110));
            gEvents.Columns.Add(Col("记录偏移", nameof(MsbEvent.Offset), 0, true));
            var events = eventSec == null ? new List<MsbEvent>() : Msb.Events(data, eventSec);
            gEvents.ItemsSource = events;

            // 区域
            var gPoints = MakeGrid();
            gPoints.IsReadOnly = false;
            gPoints.Columns.Add(Col("#", nameof(MsbPoint.Index), 60));
            gPoints.Columns.Add(Col("区域名", nameof(MsbPoint.Name), 200));
            gPoints.Columns.Add(new DataGridTextColumn { Header = "X", Width = 100,
                Binding = new Binding(nameof(MsbPoint.X)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" } });
            gPoints.Columns.Add(new DataGridTextColumn { Header = "Y", Width = 100,
                Binding = new Binding(nameof(MsbPoint.Y)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" } });
            gPoints.Columns.Add(new DataGridTextColumn { Header = "Z", Width = 100,
                Binding = new Binding(nameof(MsbPoint.Z)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" } });
            gPoints.Columns.Add(Col("类型", nameof(MsbPoint.TypeName), 0, true));
            var points = pointSec == null ? new List<MsbPoint>() : Msb.Points(data, pointSec);
            gPoints.ItemsSource = points;

            gPoints.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                int written = 0;
                foreach (var pt in points)
                {
                    var cx = Msb.ReadFloat(buf, pt.Offset, Msb.PointX);
                    var cy = Msb.ReadFloat(buf, pt.Offset, Msb.PointY);
                    var cz = Msb.ReadFloat(buf, pt.Offset, Msb.PointZ);
                    if (Math.Abs(cx - pt.X) > 1e-6) { Msb.WriteFloat(buf, pt.Offset, Msb.PointX, pt.X); written++; }
                    if (Math.Abs(cy - pt.Y) > 1e-6) { Msb.WriteFloat(buf, pt.Offset, Msb.PointY, pt.Y); written++; }
                    if (Math.Abs(cz - pt.Z) > 1e-6) { Msb.WriteFloat(buf, pt.Offset, Msb.PointZ, pt.Z); written++; }
                }
                if (written > 0)
                {
                    doc.Dirty = true;
                    StatusDirty.Text = "● 未保存";
                    StatusInfo.Text = $"区域坐标已修改 {written} 处（Ctrl+S 写入 .msb）";
                }
            }), System.Windows.Threading.DispatcherPriority.Background);

            // 部件
            var gParts = MakeGrid();
            gParts.IsReadOnly = false;
            gParts.Columns.Add(Col("#", nameof(MsbPart.Index), 60));
            gParts.Columns.Add(Col("部件名", nameof(MsbPart.Name), 190));
            gParts.Columns.Add(new DataGridTextColumn
            {
                Header = "X", Width = 90,
                Binding = new Binding(nameof(MsbPart.X)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" }
            });
            gParts.Columns.Add(new DataGridTextColumn
            {
                Header = "Y", Width = 90,
                Binding = new Binding(nameof(MsbPart.Y)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" }
            });
            gParts.Columns.Add(new DataGridTextColumn
            {
                Header = "Z", Width = 90,
                Binding = new Binding(nameof(MsbPart.Z)) { Mode = BindingMode.TwoWay, StringFormat = "0.###" }
            });
            gParts.Columns.Add(Col("ScaleX", nameof(MsbPart.ScaleX), 80));
            gParts.Columns.Add(Col("ScaleY", nameof(MsbPart.ScaleY), 80));
            gParts.Columns.Add(Col("ScaleZ", nameof(MsbPart.ScaleZ), 80));
            gParts.Columns.Add(Col("模型", nameof(MsbPart.ModelSib), 0, true));
            var parts = partSec == null ? new List<MsbPart>() : Msb.Parts(data, partSec);
            gParts.ItemsSource = parts;

            gParts.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                int written = 0;
                foreach (var p in parts)
                {
                    var cx = Msb.ReadFloat(buf, p.Offset, Msb.PartX);
                    var cy = Msb.ReadFloat(buf, p.Offset, Msb.PartY);
                    var cz = Msb.ReadFloat(buf, p.Offset, Msb.PartZ);
                    if (Math.Abs(cx - p.X) > 1e-6) { Msb.WriteFloat(buf, p.Offset, Msb.PartX, p.X); written++; }
                    if (Math.Abs(cy - p.Y) > 1e-6) { Msb.WriteFloat(buf, p.Offset, Msb.PartY, p.Y); written++; }
                    if (Math.Abs(cz - p.Z) > 1e-6) { Msb.WriteFloat(buf, p.Offset, Msb.PartZ, p.Z); written++; }
                }
                if (written > 0)
                {
                    doc.Dirty = true;
                    StatusDirty.Text = "● 未保存";
                    StatusInfo.Text = $"地图坐标已修改 {written} 处（Ctrl+S 写入 .msb）";
                }
            }), System.Windows.Threading.DispatcherPriority.Background);

            var inner = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectedIndex = Math.Clamp(_msbTabIndex, 0, 3) };
            inner.ItemContainerStyle = (Style)FindResource("VsTab");
            inner.Items.Add(MakeTab($"模型 {models.Count}",
                "模型（MODEL）：这张地图引用的静态网格与材质包。每条记录是一个模型定义，指向 .sib 骨架/网格文件；" +
                "改这里等于替换整张地图用到的模型资源。", gModels));
            inner.Items.Add(MakeTab($"事件 {events.Count}",
                "事件（EVENT）：地图上与实体模型绑定的交互事件。名字形如「PL　m2080　松明01」= 触发者(P)　关联模型　事件类型　序号。" +
                "「序号」是记录内的连续索引；「标志（推测）」未被证实，可能是事件标志位；「参数块偏移」指向名字之后的二进制参数区。", gEvents));
            inner.Items.Add(MakeTab($"区域 {points.Count}",
                "区域（POINT）：触发区域——篝火、雾门、Boss 场地、入侵/召唤点、传送点等。" +
                "记录含名称、世界坐标（X/Y/Z）与区域类型，是地图逻辑的锚点。坐标可直接改并写回。", gPoints));
            inner.Items.Add(MakeTab($"部件 {parts.Count}",
                "部件（PARTS）：把模型“放到”地图上的实例（放置物）。记录含模型引用、世界坐标 X/Y/Z（+0x14/+0x18/+0x1C）" +
                "与缩放。坐标已确认可读，但本版本仍为只读——写回要等地图编辑一起做。", gParts));

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            void Chip(string text, Brush fg)
            {
                bar.Children.Add(new TextBlock
                {
                    Text = text, Margin = new Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Center, Foreground = fg, FontSize = 12
                });
            }
            Chip(Path.GetFileName(path), (Brush)FindResource("TextBright"));
            Chip($"{new FileInfo(path).Length:N0} 字节", (Brush)FindResource("TextDim"));
            Chip($"模型 {models.Count} · 事件 {events.Count} · 区域 {points.Count} · 部件 {parts.Count}",
                 (Brush)FindResource("TextDim"));
            Chip("部件 / 区域坐标可改 · 其余只读 · 不触碰 Havok 碰撞", (Brush)FindResource("WarnFg"));

            var btnSaveMsb = new Button { Content = "保存地图 (Ctrl+S)", Padding = new Thickness(12, 3, 12, 3) };
            btnSaveMsb.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnSaveMsb.Click += (_, _) => SaveMsb(doc);
            bar.Children.Add(btnSaveMsb);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Border
            {
                Child = bar,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(inner, 1);
            root.Children.Add(inner);

            AddTab(Path.GetFileName(path), root, doc);
            StatusInfo.Text = $"MSB · 模型 {models.Count} · 事件 {events.Count} · 区域 {points.Count} · 部件 {parts.Count}";
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>事件指令表：24 字节/条，含类型 / 参数长度 / 参数池偏移与参数值。</summary>
    void ShowInstrTab(string title, byte[] data, EmevdEvent ev)
    {
        var ins = EmevdInstrs.Read(data, ev);
        if (ins.Count == 0) { StatusInfo.Text = "该事件没有可解析的指令"; return; }

        var doc = new Doc { Path = "instr:" + title + ":" + ev.Id };
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false };
        grid.SetResourceReference(StyleProperty, "VsGrid");
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(EmevdInstr.Index)), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding(nameof(EmevdInstr.Type)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "指令名（EMEDF）", Binding = new Binding(nameof(EmevdInstr.Name)), Width = 240
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "子值", Binding = new Binding(nameof(EmevdInstr.Sub)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "参数字节", Binding = new Binding(nameof(EmevdInstr.ArgBytes)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "参数池偏移", Binding = new Binding(nameof(EmevdInstr.ArgOffset)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "标志", Binding = new Binding(nameof(EmevdInstr.Flag)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "参数（按 EMEDF 类型展开）", Binding = new Binding(nameof(EmevdInstr.Args)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.ItemsSource = ins;

        var head = new StackPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        bar.Children.Add(new TextBlock
        {
            Text = $"{title} · 事件 {ev.Id} · 指令 {ins.Count} 条 · 每条 24 字节",
            Foreground = (Brush)FindResource("TextBright"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(bar);
        head.Children.Add(MakeNote(
            "指令记录固定 24 字节：类型 / 子值 / 参数字节数 / 参数池偏移 / 标志。参数池在指令区之前，" +
            "按偏移取值、按字节数截断，这里按 4 字节整数展开（不足 4 字节时显示十六进制）。" +
            "指令名与参数类型取自社区 EMEDF（soulsmods.github.io/emedf/ds1-emedf.html），246 条。参数按声明类型顺序展开，未处理结构体对齐填充，个别数值可能偏移 1–3 字节。"));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new Border
        {
            Child = head,
            BorderBrush = (Brush)FindResource("BorderSoft"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        Grid.SetRow(top, 0);
        root.Children.Add(top);
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        AddTab($"事件 {ev.Id} 指令", root, doc);
    }

    /// <summary>把一个事件的指令区以十六进制形式展示（反汇编尚未实现）。</summary>
    void ShowHexTab(string title, byte[] data, EmevdEvent ev)
    {
        long start = ev.InstrOffset;
        long end = Math.Min(data.Length, start + ev.InstrCount * 24 + 512);
        if (start < 0 || start >= data.Length) { StatusInfo.Text = "指令偏移越界"; return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"事件 {ev.Id} · 指令 {ev.InstrCount} 条 · 起始 0x{start:X} · 每行 16 字节");
        sb.AppendLine();
        for (long p = start; p + 16 <= end; p += 16)
        {
            sb.Append($"{p:X6}  ");
            for (int i = 0; i < 16; i++) sb.Append($"{data[p + i]:X2} ");
            sb.Append("  ");
            for (int i = 0; i < 16; i++)
            {
                byte b = data[p + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }

        var box = new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Background = (Brush)FindResource("EditorBg"),
            Foreground = (Brush)FindResource("TextFg"),
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap
        };

        var doc = new Doc { Path = "hex:" + title };
        AddTab(title, box, doc);
    }

    // ================= 模型域（容器 + FLVER 概要） =================

    void OpenBundleDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var rawBytes = File.ReadAllBytes(path);
            var entries = Flver.Scan(rawBytes);
            if (entries.Count == 0) { ShowErrorTab(Path.GetFileName(path), "容器内没有条目"); return; }

            var doc = new Doc { Path = path };
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false };
            grid.SetResourceReference(StyleProperty, "VsGrid");
            grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(BundleEntry.Index)), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "条目", Binding = new Binding(nameof(BundleEntry.Name)), Width = 220 });
            grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding(nameof(BundleEntry.Kind)), Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = "大小", Binding = new Binding(nameof(BundleEntry.Size)), Width = 100 });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "FLVER 概要", Binding = new Binding(nameof(BundleEntry.Detail)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.ItemsSource = entries;

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            void Chip(string text, Brush fg)
            {
                bar.Children.Add(new TextBlock
                {
                    Text = text, Margin = new Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Center, Foreground = fg, FontSize = 12
                });
            }
            Chip(Path.GetFileName(path), (Brush)FindResource("TextBright"));
            Chip($"{entries.Count} 个条目", (Brush)FindResource("TextDim"));
            Chip("可替换模型文件 · 其余只读", (Brush)FindResource("WarnFg"));

            var btnReplaceModel = new Button { Content = "替换模型…", Padding = new Thickness(12, 3, 12, 3) };
            btnReplaceModel.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnReplaceModel.Click += (_, _) => ReplaceBundleEntry(path, grid);
            bar.Children.Add(btnReplaceModel);

            var btnTree = new Button { Content = "结构图", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
            btnTree.SetResourceReference(StyleProperty, "VsBtn");
            btnTree.Click += (_, _) =>
            {
                if (grid.SelectedItem is BundleEntry be2 && be2.Kind == "flver")
                {
                    var box = new TextBox
                    {
                        Text = Flver.Tree(rawBytes, be2),
                        IsReadOnly = true,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12.5,
                        Background = (Brush)FindResource("EditorBg"),
                        Foreground = (Brush)FindResource("TextFg"),
                        BorderThickness = new Thickness(0),
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.NoWrap
                    };
                    AddTab("结构 " + be2.Name, box, new Doc { Path = "tree:" + path + ":" + be2.Name });
                }
                else StatusInfo.Text = "先选中一条 .flver 条目";
            };
            bar.Children.Add(btnTree);

            var head = new StackPanel();
            head.Children.Add(bar);
            head.Children.Add(MakeNote(
                "模型容器（*.chrbnd / *.partsbnd）。里面通常是一个 .flver（模型本体）加一个 .hkx（骨骼/物理）。" +
                "选中 .flver 后下方会显示完整头部字段与包围盒。FLVER 里没有字符串锚点（材质/骨骼名在别处），" +
                "因此骨骼表、材质表、网格表尚未解析——这需要完整的结构规范，不能靠猜。"));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Border
            {
                Child = head,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(grid, 1);
            root.Children.Add(grid);

            var detail = new TextBox
            {
                IsReadOnly = true,
                Height = 190,
                Margin = new Thickness(0, 1, 0, 0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11.5,
                Background = (Brush)FindResource("EditorBg"),
                Foreground = (Brush)FindResource("TextDim"),
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Text = "选中一条 .flver 查看头部字段"
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(detail, 2);
            root.Children.Add(detail);

            grid.SelectionChanged += (_, _) =>
            {
                if (grid.SelectedItem is BundleEntry be && be.Header.Length > 0) detail.Text = be.Header;
                else detail.Text = "该条目不是 FLVER（或魔数不符）";
            };

            int firstFlver = entries.FindIndex(x => x.Kind == "flver");
            grid.SelectedIndex = firstFlver >= 0 ? firstFlver : 0;

            AddTab(Path.GetFileName(path), root, doc);
            StatusInfo.Text = $"模型容器 · {entries.Count} 个条目";
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ================= 动画域（TAE 概览） =================

    void OpenTaeDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var f = Tae.Parse(File.ReadAllBytes(path));
            if (f.Size == 0) { ShowErrorTab(Path.GetFileName(path), f.Note.Length > 0 ? f.Note : "没有 TAE"); return; }

            var doc = new Doc { Path = path };

            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, MaxHeight = 240 };
            grid.SetResourceReference(StyleProperty, "VsGrid");
            grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(TaeAnim.Index)), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "动画 ID", Binding = new Binding(nameof(TaeAnim.AnimId)), Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = "事件数", Binding = new Binding(nameof(TaeAnim.EventCount)), Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = "事件表偏移", Binding = new Binding(nameof(TaeAnim.EventTableOffset)), Width = 120 });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "事件数组偏移", Binding = new Binding(nameof(TaeAnim.EventArrayOffset)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.ItemsSource = f.Anims;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TAE {f.Note} · {f.Size:N0} 字节 · 魔数 {f.Magic} · 动画 {f.Anims.Count}");
            sb.AppendLine();
            sb.AppendLine("头部字段");
            foreach (var kv in f.Header) sb.AppendLine($"  {kv.Key,-10} = {kv.Value}");
            sb.AppendLine();
            sb.AppendLine($"动画 / 骨架名称（{f.Names.Count} 个）");
            foreach (var n in f.Names) sb.AppendLine("  " + n);
            sb.AppendLine();
            sb.AppendLine("原始字节（前 4 KB）");
            sb.Append(f.Hex);

            var box = new TextBox
            {
                Text = sb.ToString(),
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11.5,
                Background = (Brush)FindResource("EditorBg"),
                Foreground = (Brush)FindResource("TextDim"),
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap
            };

            var head = new StackPanel();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            bar.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(path)} · 动画 {f.Names.Count} 个 · 只读",
                Foreground = (Brush)FindResource("TextBright"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(bar);
            head.Children.Add(MakeNote(
                "TAE 是动画事件表：记录每个动画里在哪些帧触发什么（受击帧、无敌帧、音效帧、位移帧）。" +
                "动画清单（ID / 事件数 / 偏移）已按 8 字节步长自校验解析；事件记录本身是变长指针结构，" +
                "要解出「类型 → 起止帧」还需动画事件模板，尚未接入。"));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Border
            {
                Child = head,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(grid, 1);
            root.Children.Add(grid);
            Grid.SetRow(box, 2);
            root.Children.Add(box);

            AddTab(Path.GetFileName(path), root, doc);
            StatusInfo.Text = $"TAE · {f.Size:N0} 字节 · 名称 {f.Names.Count} 个";
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ================= 快捷修改（集成自 DSRModifier） =================

    void MenuQuick_Click(object sender, RoutedEventArgs e) => OpenQuickDoc();

    void OpenQuickDoc()
    {
        var existing = _docs.FirstOrDefault(d => d.Path == "quick:dsrmodifier");
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        var doc = new Doc { Path = "quick:dsrmodifier" };
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        root.Children.Add(new TextBlock
        {
            Text = "快捷修改（集成自 DSRModifier）",
            FontSize = 15,
            Foreground = (Brush)FindResource("TextBright"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(MakeNote(
            "每个按钮直接改 param/GameParam.parambnd.dcx：自动快照 → 写入 → 回读校验 → 失败回滚。" +
            "改动行数与缺失字段都会写进日志；想撤销就在快照目录里找回原文件。"));

        var log = new TextBox
        {
            IsReadOnly = true,
            Height = 300,
            Margin = new Thickness(0, 10, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Background = (Brush)FindResource("EditorBg"),
            Foreground = (Brush)FindResource("TextDim"),
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Text = "（点击按钮后这里显示执行日志）"
        };

        var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        void AddOp(string label, string op)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 5, 14, 5) };
            b.SetResourceReference(StyleProperty, "VsBtn");
            b.Click += (_, _) => log.Text = RunQuick(op) + "\n\n" + log.Text;
            wrap.Children.Add(b);
        }
        AddOp("附魔全开（全部武器 isEnhance=1）", "enhance");
        AddOp("法术次数 999", "magic999");
        AddOp("武器补正统一 99", "correct99");
        AddOp("防具韧性 ×2", "sa2x");
        root.Children.Add(wrap);
        root.Children.Add(log);

        AddTab("快捷修改", new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }, doc);
    }

    string RunQuick(string op)
    {
        var path = Path.Combine(_game.Root, "param", "GameParam", "GameParam.parambnd.dcx");
        try
        {
            var raw = File.ReadAllBytes(path);
            var buf = Dcx.Decompress(raw);
            var defs = Params.LoadDefs(File.ReadAllBytes(
                Path.Combine(_game.Root, "paramdef", "paramdef.paramdefbnd.dcx")));
            var tables = Params.LoadTables(buf, defs);

            int changed = 0;
            var missing = new List<string>();

            void Set(string table, string field, string value, Func<string, string> transform = null)
            {
                if (!tables.TryGetValue(table, out var t)) { missing.Add(table); return; }
                var f = t.Def.Fields.FirstOrDefault(x =>
                    x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (f == null) { missing.Add(table + "." + field); return; }
                foreach (var r in t.Rows)
                {
                    var cur = Params.Format(buf, r, f);
                    var nv = transform != null ? transform(cur) : value;
                    if (nv == null || nv == cur) continue;
                    if (Params.TryWrite(buf, r, f, nv, out _)) changed++;
                }
            }

            switch (op)
            {
                case "enhance":
                    Set("EQUIP_PARAM_WEAPON_ST", "isEnhance", "1");
                    break;
                case "magic999":
                    Set("MAGIC_PARAM_ST", "maxQuantity", "999");
                    break;
                case "correct99":
                    Set("EQUIP_PARAM_WEAPON_ST", "correctStrength", "99");
                    Set("EQUIP_PARAM_WEAPON_ST", "correctAgility", "99");
                    Set("EQUIP_PARAM_WEAPON_ST", "correctMagic", "99");
                    Set("EQUIP_PARAM_WEAPON_ST", "correctFaith", "99");
                    break;
                case "sa2x":
                    Set("EQUIP_PARAM_PROTECTOR_ST", "saDurability", null, cur =>
                        double.TryParse(cur, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double dv)
                            ? ((int)(dv * 2)).ToString() : cur);
                    break;
                default:
                    return $"[{op}] 未知操作";
            }

            if (changed == 0)
                return $"[{op}] 没有改动任何行" +
                       (missing.Count > 0 ? $"；缺少：{string.Join(", ", missing)}" : "");

            var snap = Snapshot(path);
            var bytes = Dcx.Compress(buf, raw);
            File.WriteAllBytes(path, bytes);

            var check = Dcx.Decompress(File.ReadAllBytes(path));
            bool ok = check.Length == buf.Length;
            if (ok)
                for (int i = 0; i < check.Length; i++)
                    if (check[i] != buf[i]) { ok = false; break; }
            if (!ok)
            {
                File.Copy(snap, path, true);
                return $"[{op}] 写后校验失败，已回滚";
            }

            return $"[{op}] 改 {changed} 处 · {bytes.Length:N0} 字节 · 快照 {Path.GetFileName(snap)} · 回读校验一致" +
                   (missing.Count > 0 ? $"\n  （跳过缺失字段：{string.Join(", ", missing)}）" : "");
        }
        catch (Exception ex)
        {
            return $"[{op}] 异常：{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ================= 事件域（只读） =================

    void OpenEmevdDoc(string path)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == path);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            var file = Emevd.Parse(File.ReadAllBytes(path));
            if (file.Events.Count == 0)
            {
                ShowErrorTab(Path.GetFileName(path), file.Warning.Length > 0 ? file.Warning : "未解析到事件");
                return;
            }

            var doc = new Doc { Path = path };
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false };
            grid.SetResourceReference(StyleProperty, "VsGrid");
            grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(EmevdEvent.Index)), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "事件 ID", Binding = new Binding(nameof(EmevdEvent.Id)), Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = "指令数", Binding = new Binding(nameof(EmevdEvent.InstrCount)), Width = 90 });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "指令区偏移", Binding = new Binding(nameof(EmevdEvent.InstrOffset)), Width = 130
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "重启类型", Binding = new Binding(nameof(EmevdEvent.RestBehavior)), Width = 100
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "备注", Binding = new Binding(nameof(EmevdEvent.Note)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.ItemsSource = file.Events;

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            void Chip(string text, Brush fg)
            {
                bar.Children.Add(new TextBlock
                {
                    Text = text, Margin = new Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Center, Foreground = fg, FontSize = 12
                });
            }
            Chip(Path.GetFileName(path), (Brush)FindResource("TextBright"));
            Chip($"{new FileInfo(path).Length:N0} 字节（解包 {file.FileSize:N0}）", (Brush)FindResource("TextDim"));
            Chip($"事件 {file.Events.Count}", (Brush)FindResource("TextDim"));
            Chip("只读 · 未反汇编（可看原始字节）", (Brush)FindResource("WarnFg"));

            var btnBytes = new Button { Content = "查看事件指令", Padding = new Thickness(12, 3, 12, 3) };
            btnBytes.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnBytes.Click += (_, _) =>
            {
                if (grid.SelectedItem is not EmevdEvent ev) { StatusInfo.Text = "先在表里选中一条事件"; return; }
                ShowInstrTab(Path.GetFileName(path), file.Raw, ev);
            };
            bar.Children.Add(btnBytes);

            var btnHex = new Button { Content = "原始字节", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
            btnHex.SetResourceReference(StyleProperty, "VsBtn");
            btnHex.Click += (_, _) =>
            {
                if (grid.SelectedItem is not EmevdEvent ev) { StatusInfo.Text = "先在表里选中一条事件"; return; }
                ShowHexTab($"{Path.GetFileName(path)} · 事件 {ev.Id}", file.Raw, ev);
            };
            bar.Children.Add(btnHex);

            var head = new StackPanel();
            head.Children.Add(bar);
            head.Children.Add(MakeNote(
                "事件脚本（EMEVD）。每一行是一个事件：事件 ID 由地图/实体参数引用，指令数是这个事件包含的字节码条数，" +
                "指令区偏移指向字节码起点（+0x08）。重启类型 -1 表示默认。当前只解析事件表，指令尚未反汇编。"));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Border
            {
                Child = head,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(grid, 1);
            root.Children.Add(grid);

            AddTab(Path.GetFileName(path), root, doc);
            StatusInfo.Text = $"EMEVD · 事件 {file.Events.Count} 条";
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(path), ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>把用户选的 .flver 塞回 BND 容器（校验 → 回包 → 快照 → 回读校验）。</summary>
    void ReplaceBundleEntry(string containerPath, DataGrid grid)
    {
        if (grid.SelectedItem is not BundleEntry be)
        {
            StatusInfo.Text = "先选中要替换的条目";
            return;
        }

        var dlg = new OpenFileDialog { Filter = "FLVER 模型|*.flver", Title = "选择新的 " + be.Name };
        if (dlg.ShowDialog() != true) return;

        byte[] newBlob;
        try { newBlob = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) { StatusInfo.Text = "读取失败：" + ex.Message; return; }

        if (newBlob.Length < 0x20 || newBlob[0] != (byte)'F' || newBlob[1] != (byte)'L'
            || newBlob[2] != (byte)'V' || newBlob[3] != (byte)'E' || newBlob[4] != (byte)'R')
        {
            StatusInfo.Text = "选中的文件不是 FLVER（魔数不符）";
            return;
        }

        try
        {
            var raw = File.ReadAllBytes(containerPath);
            var bnd = Dcx.Decompress(raw);

            // 与容器内原条目比对头部：版本必须一致，计数不一致时提示但允许
            var entries = Bnd3.Entries(bnd);
            var same = entries.FirstOrDefault(x => string.Equals(x.Name, be.Name, StringComparison.OrdinalIgnoreCase));
            if (same != null && same.Size >= 0x20)
            {
                uint oldVer = BitConverter.ToUInt32(bnd, same.Offset + 0x08);
                uint newVer = BitConverter.ToUInt32(newBlob, 0x08);
                if (oldVer != newVer)
                {
                    StatusInfo.Text = $"FLVER 版本不符：容器 {oldVer:X8} vs 新文件 {newVer:X8}";
                    return;
                }
                uint oldMat = BitConverter.ToUInt32(bnd, same.Offset + 0x18);
                uint newMat = BitConverter.ToUInt32(newBlob, 0x18);
                if (oldMat != newMat && MessageBox.Show(this,
                        $"材质/骨骼计数变了（{oldMat} → {newMat}）。继续通常会导致白模或蒙皮错乱，仍要继续吗？",
                        "计数不一致", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                    return;
            }

            var res = Bnd3Edit.Replace(bnd, be.Name, newBlob);
            if (!res.Ok) { StatusInfo.Text = "回包失败：" + res.Error; return; }

            var verify = Bnd3Edit.Verify(res.Data, be.Name, newBlob);
            if (verify.Length > 0) { StatusInfo.Text = "回包校验失败：" + verify; return; }

            var outBytes = Dcx.IsDcx(raw) ? Dcx.Compress(res.Data, raw) : res.Data;

            var snap = Snapshot(containerPath);
            File.WriteAllBytes(containerPath, outBytes);

            // 写后回读：重新解包，确认条目内容一致
            var check = Dcx.Decompress(File.ReadAllBytes(containerPath));
            var v2 = Bnd3Edit.Verify(check, be.Name, newBlob);
            if (v2.Length > 0)
            {
                File.Copy(snap, containerPath, true);
                StatusInfo.Text = "写后校验失败，已回滚：" + v2;
                return;
            }

            StatusInfo.Text = $"已替换 {be.Name}：{res.OldSize:N0} → {res.NewSize:N0} 字节（容器），快照 {Path.GetFileName(snap)}";
        }
        catch (Exception ex)
        {
            StatusInfo.Text = "替换失败：" + ex.Message;
        }
    }

    // ================= 参数域 =================

    // ---------- 贴图命令行（验证/脚本用） ----------

    static string TexLog => Path.Combine(Path.GetTempPath(), "dsrstudio_tex.txt");

    void DoTexExport(string tpfPath, string indexText, string outPath)
    {
        try
        {
            if (!int.TryParse(indexText, out int index)) { File.WriteAllText(TexLog, "index 无效"); return; }
            var raw = File.ReadAllBytes(tpfPath);
            var buf = Dcx.Decompress(raw);
            var texs = Tpf.Textures(buf, raw);
            if (index < 0 || index >= texs.Count) { File.WriteAllText(TexLog, $"index 越界（共 {texs.Count}）"); return; }

            var t = texs[index];
            int block = Tpf.BlockBytesOf(t.FourCC, t.Dxgi);
            bool rawFmt = block == 0;
            int chain = Tpf.ChainSize(t.Width, t.Height, block, rawFmt);
            if (chain <= 0 || t.DataOffset + chain > buf.Length) { File.WriteAllText(TexLog, "数据不完整"); return; }

            var data = new byte[chain];
            Buffer.BlockCopy(buf, t.DataOffset, data, 0, chain);
            var dds = Tpf.BuildDds(t.Width, t.Height, t.FourCC, t.Dxgi, data);
            File.WriteAllBytes(outPath, dds);

            File.WriteAllText(TexLog,
                $"导出 [{index}] {t.SizeText} {t.Format} → {outPath}\n" +
                $"chain={chain} dds={dds.Length} fourCC={t.FourCC} dxgi={t.Dxgi}");
        }
        catch (Exception ex) { try { File.WriteAllText(TexLog, "异常: " + ex.Message); } catch { } }
    }

    void DoTexReplace(string tpfPath, string indexText, string ddsPath)
    {
        try
        {
            if (!int.TryParse(indexText, out int index)) { File.WriteAllText(TexLog, "index 无效"); return; }
            var raw = File.ReadAllBytes(tpfPath);
            var buf = Dcx.Decompress(raw);
            var texs = Tpf.Textures(buf, raw);
            if (index < 0 || index >= texs.Count) { File.WriteAllText(TexLog, $"index 越界（共 {texs.Count}）"); return; }

            var t = texs[index];
            int block = Tpf.BlockBytesOf(t.FourCC, t.Dxgi);
            bool rawFmt = block == 0;
            int chain = Tpf.ChainSize(t.Width, t.Height, block, rawFmt);

            var dds = File.ReadAllBytes(ddsPath);
            var info = Tpf.ParseDds(dds);
            if (info == null) { File.WriteAllText(TexLog, "DDS 解析失败"); return; }
            if (info.Width != t.Width || info.Height != t.Height)
            {
                File.WriteAllText(TexLog, $"尺寸不符 {info.Width}x{info.Height} vs {t.Width}x{t.Height}");
                return;
            }
            if (info.DataLength < chain)
            {
                File.WriteAllText(TexLog, $"数据不足 需要 {chain} 实际 {info.DataLength}");
                return;
            }

            var before = new byte[chain];
            Buffer.BlockCopy(buf, t.DataOffset, before, 0, chain);

            Buffer.BlockCopy(dds, info.DataOffset, buf, t.DataOffset, chain);
            var snap = Snapshot(tpfPath);
            var written = Dcx.Compress(buf, raw);
            File.WriteAllBytes(tpfPath, written);

            var verify = Dcx.Decompress(File.ReadAllBytes(tpfPath));
            bool ok = verify.Length == buf.Length;
            if (ok)
            {
                for (int i = 0; i < buf.Length; i++)
                    if (verify[i] != buf[i]) { ok = false; break; }
            }
            if (!ok) { File.Copy(snap, tpfPath, true); File.WriteAllText(TexLog, "校验失败已回滚"); return; }

            int changed = 0;
            for (int i = 0; i < chain; i++) if (before[i] != buf[t.DataOffset + i]) changed++;

            File.WriteAllText(TexLog,
                $"替换 [{index}] {t.SizeText} {t.Format} ← {Path.GetFileName(ddsPath)}\n" +
                $"chain={chain} 变更字节={changed} 文件={written.Length} 快照={Path.GetFileName(snap)}\n校验=通过");
        }
        catch (Exception ex) { try { File.WriteAllText(TexLog, "异常: " + ex.GetType().Name + ": " + ex.Message); } catch { } }
    }

    void DoBndReplace(string containerPath, string entryName, string flverPath)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "dsrstudio_bnd.txt");
        try
        {
            var newBlob = File.ReadAllBytes(flverPath);
            var raw = File.ReadAllBytes(containerPath);
            var bnd = Dcx.Decompress(raw);

            var res = Bnd3Edit.Replace(bnd, entryName, newBlob);
            if (!res.Ok) { File.WriteAllText(logPath, "回包失败: " + res.Error); return; }

            var v = Bnd3Edit.Verify(res.Data, entryName, newBlob);
            if (v.Length > 0) { File.WriteAllText(logPath, "回包校验失败: " + v); return; }

            var outBytes = Dcx.IsDcx(raw) ? Dcx.Compress(res.Data, raw) : res.Data;
            var snapDir = Path.Combine(Path.GetDirectoryName(containerPath)!,
                Path.GetFileName(Path.GetDirectoryName(containerPath)!) + "_snapshot");
            Directory.CreateDirectory(snapDir);
            var snap = Path.Combine(snapDir, Path.GetFileName(containerPath) + "." + DateTime.Now.ToString("HHmmss") + ".bak");
            File.Copy(containerPath, snap, true);

            File.WriteAllBytes(containerPath, outBytes);

            var check = Dcx.Decompress(File.ReadAllBytes(containerPath));
            var v2 = Bnd3Edit.Verify(check, entryName, newBlob);

            File.WriteAllText(logPath,
                $"条目={entryName} 新大小={newBlob.Length} 容器={res.OldSize}→{res.NewSize} 文件={outBytes.Length}\n" +
                $"快照={Path.GetFileName(snap)} 写后校验={(v2.Length == 0 ? "通过" : v2)}");
        }
        catch (Exception ex) { try { File.WriteAllText(logPath, "异常: " + ex.GetType().Name + ": " + ex.Message); } catch { } }
    }

    void LoadParamBundle()
    {
        if (_paramTables != null) return;
        var path = Path.Combine(_game.Root, "param", "GameParam", "GameParam.parambnd.dcx");
        var defsPath = Path.Combine(_game.Root, "paramdef", "paramdef.paramdefbnd.dcx");
        if (!File.Exists(path) || !File.Exists(defsPath))
            throw new FileNotFoundException("缺少 GameParam 或 paramdef 文件");

        var defs = Params.LoadDefs(File.ReadAllBytes(defsPath));
        var rawBytes = File.ReadAllBytes(path);
        _paramRaw = rawBytes;
        _paramBuf = Dcx.Decompress(rawBytes);
        _paramTables = Params.LoadTables(rawBytes, defs);
    }

    static string FriendlyTable(string raw)
    {
        var s = raw.Replace("_PARAM_ST", "", StringComparison.OrdinalIgnoreCase).Replace("_", " ");
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpper(p[0]) + p[1..].ToLowerInvariant()));
    }

    sealed class ParamRowVm
    {
        public uint Id { get; init; }
        public string[] Cells { get; init; } = Array.Empty<string>();
        public ParamRow Row { get; init; } = null!;
        public List<ParamField> Fields { get; init; } = new();
        public DataGrid? Grid { get; init; }
    }

    void OpenParamDoc(string tableName)
    {
        var existing = _docs.FirstOrDefault(d => d.Path == "param:" + tableName);
        if (existing != null) { Tabs.SelectedIndex = _docs.IndexOf(existing); return; }

        try
        {
            LoadParamBundle();
            if (_paramTables == null) return;
            if (!_paramTables.TryGetValue(tableName, out var table))
            {
                table = _paramTables.FirstOrDefault(kv =>
                    FriendlyTable(kv.Key).Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals(tableName, StringComparison.OrdinalIgnoreCase)).Value;
            }
            if (table == null)
            {
                MessageBox.Show(this, "找不到参数表：" + tableName, "提示");
                return;
            }

            var buf = _paramBuf!;
            var fields = table.Def?.Fields ?? new List<ParamField>();
            int stride = table.RowStride;

            var doc = new Doc { Path = "param:" + tableName };

            var grid = new DataGrid { IsReadOnly = false, CanUserSortColumns = true };
            grid.SetResourceReference(StyleProperty, "VsGrid");
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "ID", Binding = new Binding(nameof(ParamRowVm.Id)), Width = 80, IsReadOnly = true
            });

            int col = 0;
            foreach (var f in fields)
            {
                if (f.Offset + Math.Max(1, f.Size) > stride) break;
                if (col >= 48) break;
                int index = col;
                var header = new TextBlock
                {
                    Text = f.Label,
                    ToolTip = $"{f.Name}\n类型 {f.Type} · {f.Size} 字节" +
                              (f.IsBitfield ? $"\n位域：第 {f.BitOffset} 位起，占 {f.BitCount} 位（同字节其它位不受影响）"
                                            : "\n非位域：按类型整体写入")
                };
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding($"Cells[{index}]") { Mode = BindingMode.TwoWay },
                    Width = 92,
                    IsReadOnly = false
                });
                col++;
            }

            var rows = new List<ParamRowVm>(table.Rows.Count);
            foreach (var r in table.Rows)
            {
                var cells = new string[col];
                for (int c = 0; c < col; c++) cells[c] = Params.Format(buf, r, fields[c]);
                rows.Add(new ParamRowVm { Id = r.Id, Cells = cells, Row = r, Fields = fields.Take(col).ToList() });
            }
            grid.ItemsSource = rows;

            grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                int written = 0; string firstError = "";
                foreach (var vm in rows)
                {
                    for (int c = 0; c < vm.Fields.Count && c < vm.Cells.Length; c++)
                    {
                        var f = vm.Fields[c];
                        var shown = Params.Format(buf, vm.Row, f);
                        if (vm.Cells[c] == shown) continue;
                        if (Params.TryWrite(buf, vm.Row, f, vm.Cells[c], out string err))
                        {
                            written++;
                        }
                        else
                        {
                            if (firstError == "") firstError = $"ID {vm.Id} / {f.Name}: {err}";
                            vm.Cells[c] = shown;
                        }
                    }
                }
                if (written > 0)
                {
                    _paramDirty = true;
                    StatusDirty.Text = "● 未保存";
                    StatusInfo.Text = $"已修改 {written} 处（Ctrl+S 写入磁盘）";
                    grid.Items.Refresh();
                }
                else if (firstError != "")
                {
                    StatusInfo.Text = firstError;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            void Chip(string text, Brush fg, double right = 14)
            {
                bar.Children.Add(new TextBlock
                {
                    Text = text, Margin = new Thickness(0, 0, right, 0),
                    VerticalAlignment = VerticalAlignment.Center, Foreground = fg, FontSize = 12
                });
            }
            Chip(FriendlyTable(tableName), (Brush)FindResource("TextBright"));
            Chip($"{table.Rows.Count} 行", (Brush)FindResource("TextDim"));
            Chip($"{table.Def?.Fields.Count ?? 0} 字段", (Brush)FindResource("TextDim"));
            int declared = table.Def?.DeclaredStride ?? 0;
            string okText = declared == stride ? $"字段累计 {declared} = 行宽 {stride} ✓" : $"字段累计 {declared} ≠ 行宽 {stride}";
            Chip(okText, okText.Contains('✓') ? (Brush)FindResource("OkFg") : (Brush)FindResource("WarnFg"));

            var btnSave = new Button { Content = "写入参数文件 (Ctrl+S)", Padding = new Thickness(12, 3, 12, 3) };
            btnSave.SetResourceReference(StyleProperty, "VsPrimaryBtn");
            btnSave.Click += (_, _) => { Tabs.SelectedItem = _tabs.FirstOrDefault(t => _docs[_tabs.IndexOf(t)]?.Path == "param:" + tableName) ?? Tabs.SelectedItem; SaveParams(); };
            bar.Children.Add(btnSave);

            var search = new TextBox
            {
                Width = 180,
                Height = 24,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "按 ID 过滤（留空显示全部）"
            };
            search.SetResourceReference(StyleProperty, "VsInput");
            bar.Children.Add(search);

            var paramHead = new StackPanel();
            paramHead.Children.Add(bar);
            paramHead.Children.Add(MakeNote(
                "这是游戏参数表（PARAM）。每行是一条数据、第一列是 ID；列名来自 paramdef，鼠标悬停列头可看到类型与字节数。" +
                "「✓/·」表示 1 位开关，「字段累计 = 行宽」是用来自检列偏移是否对齐的。改完按 Ctrl+S 写盘（自动快照 + 回读校验）。"));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Border
            {
                Child = paramHead,
                BorderBrush = (Brush)FindResource("BorderSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Grid.SetRow(top, 0);
            root.Children.Add(top);
            Grid.SetRow(grid, 1);
            root.Children.Add(grid);

            search.TextChanged += (_, _) =>
            {
                var q = search.Text.Trim();
                grid.ItemsSource = q.Length == 0
                    ? rows
                    : rows.Where(r => r.Id.ToString().Contains(q, StringComparison.Ordinal)).ToList();
                StatusInfo.Text = q.Length == 0 ? $"{rows.Count} 行" : $"匹配 {grid.Items.Count} / {rows.Count} 行";
            };

            AddTab(FriendlyTable(tableName), root, doc);
        }
        catch (Exception ex)
        {
            ShowErrorTab(Path.GetFileName(tableName), ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 命令行写回：--param-set &lt;表名&gt; &lt;行ID&gt; &lt;字段名&gt; &lt;新值&gt;
    /// 结果写到 %TEMP%\dsrstudio_paramset.txt，便于脚本验证。
    /// </summary>
    void DoParamSet(string tableName, string idText, string fieldName, string value)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "dsrstudio_paramset.txt");
        try
        {
            LoadParamBundle();
            if (_paramTables == null) { File.WriteAllText(logPath, "参数未加载"); return; }

            if (!_paramTables.TryGetValue(tableName, out var table))
            {
                table = _paramTables.FirstOrDefault(kv =>
                    FriendlyTable(kv.Key).Equals(tableName, StringComparison.OrdinalIgnoreCase)).Value;
            }
            if (table?.Def == null) { File.WriteAllText(logPath, "找不到表: " + tableName); return; }

            if (!uint.TryParse(idText, out uint id)) { File.WriteAllText(logPath, "ID 无效"); return; }
            var row = table.Rows.FirstOrDefault(r => r.Id == id);
            if (row == null) { File.WriteAllText(logPath, "找不到行: " + id); return; }

            var field = table.Def.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field == null)
            {
                File.WriteAllText(logPath, "找不到字段: " + fieldName + "\n可选: " +
                    string.Join(",", table.Def.Fields.Take(30).Select(f => f.Name)));
                return;
            }

            var before = Params.Format(_paramBuf!, row, field);
            if (!Params.TryWrite(_paramBuf!, row, field, value, out string err))
            {
                File.WriteAllText(logPath, "写入失败: " + err);
                return;
            }
            var after = Params.Format(_paramBuf!, row, field);
            SaveParams();
            File.WriteAllText(logPath,
                $"表={table.Name} 行={id} 字段={field.Name} 原值={before} 新值={after}\n" +
                $"状态={StatusInfo.Text}");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(logPath, "异常: " + ex.GetType().Name + ": " + ex.Message); } catch { }
        }
    }

    void SaveMsb(Doc doc)
    {
        if (doc.Buffer.Length == 0) { StatusInfo.Text = "没有可保存的地图数据"; return; }
        try
        {
            var snap = Snapshot(doc.Path);
            File.WriteAllBytes(doc.Path, doc.Buffer);

            // 回读校验：文件内容必须与内存缓冲一致
            var check = File.ReadAllBytes(doc.Path);
            bool ok = check.Length == doc.Buffer.Length;
            if (ok)
            {
                for (int i = 0; i < check.Length; i++)
                    if (check[i] != doc.Buffer[i]) { ok = false; break; }
            }
            if (!ok)
            {
                File.Copy(snap, doc.Path, true);
                StatusInfo.Text = "MSB 写入校验失败，已回滚";
                return;
            }

            doc.Dirty = false;
            StatusDirty.Text = "";
            StatusInfo.Text = $"地图已写入 {check.Length:N0} 字节 · 快照 {Path.GetFileName(snap)} · 回读一致";
        }
        catch (Exception ex)
        {
            StatusInfo.Text = "地图写入失败：" + ex.Message;
        }
    }

    void SaveParams()
    {
        if (_paramBuf == null || _paramRaw == null || _paramTables == null)
        {
            StatusInfo.Text = "参数尚未加载";
            return;
        }
        var path = Path.Combine(_game.Root, "param", "GameParam", "GameParam.parambnd.dcx");
        try
        {
            var snap = Snapshot(path);
            var bytes = Dcx.Compress(_paramBuf, _paramRaw);
            File.WriteAllBytes(path, bytes);

            // 写后回读校验：重新解包，逐表核对行数与若干字段
            var check = Dcx.Decompress(File.ReadAllBytes(path));
            bool ok = check.Length == _paramBuf.Length;
            int checkedFields = 0;
            if (ok)
            {
                for (int i = 0; i < check.Length; i++)
                {
                    if (check[i] != _paramBuf[i]) { ok = false; break; }
                }
            }
            if (!ok)
            {
                File.Copy(snap, path, true);
                StatusInfo.Text = "写入校验失败，已从快照回滚";
                return;
            }

            _paramRaw = bytes;
            _paramDirty = false;
            StatusDirty.Text = "";
            StatusInfo.Text = $"参数已写入 {bytes.Length:N0} 字节 · 快照 {Path.GetFileName(snap)} · 回读校验一致";
        }
        catch (Exception ex)
        {
            StatusInfo.Text = "写入失败：" + ex.Message;
            ShowErrorTab("参数写入", ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ================= 其他域（占位） =================

    void OpenInfoDoc(Item it)
    {
        var doc = new Doc { Path = it.Path };
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Line(string text, string style = "UiText", double top = 8)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.StyleProperty, style);
            panel.Children.Add(tb);
        }

        Line(Path.GetFileName(it.Path), "UiText", 0);
        Line(Rel(it.Path), "MonoText", 4);
        Line(_domain switch
        {
            "param" => "参数域：需要 PARAMDEF 字段表才能把行数据映射成命名字段。\n旧实现随游戏目录重装丢失，v0.2 重新移植后即可表格化编辑。",
            "map" => "地图域：MSB 记录模型/放置/region/事件引用。\nv0.2 先做只读浏览；Havok 碰撞（hkx）不在修改范围内。",
            "event" => "事件域：EMEVD 每个地图一份，Common 全局生效。\nv0.3 接入事件树 + 指令反汇编，新事件 ID 从 vanilla 派生。",
            "model" => "模型域：*.chrbnd / *.partsbnd 内含 FLVER。\nv0.3 只读浏览骨骼、材质、网格；命中判定不随视觉改变。",
            _ => "该域尚未接入。"
        }, "DimText", 16);
        Line($"文件大小：{new FileInfo(it.Path).Length:N0} 字节", "DimText", 16);

        AddTab(Path.GetFileName(it.Path), panel, doc);
    }

    // ================= 保存 / 回滚 =================

    void Save_Click(object sender, RoutedEventArgs e) => SaveDoc();
    void Rollback_Click(object sender, RoutedEventArgs e) { var d = CurrentDoc(); if (d != null) RollbackDoc(d); }

    void SaveDoc()
    {
        var doc = CurrentDoc();
        if (doc == null) { StatusInfo.Text = "没有打开的标签"; return; }
        if (doc.Path.EndsWith(".msb", StringComparison.OrdinalIgnoreCase))
        {
            SaveMsb(doc);
            return;
        }
        if (doc.Path.StartsWith("param:", StringComparison.Ordinal))
        {
            SaveParams();
            return;
        }
        if (doc.State is not TextState) { StatusInfo.Text = "该标签为只读视图，无可保存内容"; return; }
        try
        {
            var snap = Snapshot(doc.Path);
            var bytes = Dcx.Compress(doc.Buffer, doc.Raw);
            File.WriteAllBytes(doc.Path, bytes);
            doc.Raw = bytes;
            MarkDirty(doc, false);
            StatusInfo.Text = $"已保存 {Path.GetFileName(doc.Path)}（{bytes.Length:N0} 字节） · 快照 {Path.GetFileName(snap)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    string Snapshot(string path)
    {
        var dir = Path.Combine(_game.Root, "_ModDev", "snapshots", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, Path.GetFileName(path));
        File.Copy(path, dst, true);
        return dst;
    }

    void RollbackDoc(Doc doc)
    {
        var dir = Path.Combine(_game.Root, "_ModDev", "snapshots");
        if (!Directory.Exists(dir)) { StatusInfo.Text = "没有可用快照"; return; }
        var latest = Directory.EnumerateFiles(dir, Path.GetFileName(doc.Path), SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (latest == null) { StatusInfo.Text = "没有该文件的快照"; return; }
        RestoreSnapshot(latest);
    }

    void RestoreSnapshot(string snapshotPath)
    {
        var name = Path.GetFileName(snapshotPath);
        var candidates = Directory.EnumerateFiles(_game.Root, name, SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "_ModDev" + Path.DirectorySeparatorChar))
            .ToList();
        if (candidates.Count == 0) { StatusInfo.Text = "找不到原始位置的同名文件：" + name; return; }

        var target = candidates.Count == 1 ? candidates[0] :
            candidates.FirstOrDefault(p => p.Contains("SCHINESE")) ?? candidates[0];

        if (MessageBox.Show(this, $"把快照写回：\n{Rel(target)}\n\n来源：{snapshotPath}", "回滚确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        try
        {
            File.Copy(snapshotPath, target, true);
            StatusInfo.Text = $"已回滚 {Path.GetFileName(target)}（重启游戏生效）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "回滚失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================= 菜单 =================

    void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择 DARK SOULS REMASTERED 安装目录" };
        if (dlg.ShowDialog() != true) return;
        _game = new GameRoot(dlg.FolderName);
        StatusRoot.Text = _game.Root;
        EmptyRoot.Text = _game.Root;
        LoadDomain(_domain);
    }

    void Rescan_Click(object sender, RoutedEventArgs e) => LoadDomain(_domain);

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    void FocusExplorer_Click(object sender, RoutedEventArgs e) => SideList.Focus();

    void CloseTab_Click(object sender, RoutedEventArgs e) => CloseCurrentTab();

    void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "DSR Studio v0.1\n\n" +
            "统一修改工作台（VS Code 风格外壳）\n" +
            "已接入：文本 / FMG、纹理 / TPF（预览）、备份与回滚\n" +
            "接入中：参数 / PARAM、地图 / MSB、事件 / EMEVD、模型 / FLVER\n\n" +
            "原则：写入前快照、只做可回滚修改、不触碰 Havok 碰撞。",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
}
