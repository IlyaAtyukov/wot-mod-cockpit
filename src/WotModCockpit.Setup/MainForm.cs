using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Окно сетапа: папка игры, список модов с галочками, кнопка «Установить».
    /// <para>
    /// Собрано кодом, без дизайнера: окно одно, а файл .Designer.cs, который правят мышью,
    /// здесь только мешал бы читать. Сама установка — в <see cref="ModInstaller"/>; окно лишь
    /// спрашивает и показывает.
    /// </para>
    /// </summary>
    internal sealed class MainForm : Form
    {
        private static readonly Color DangerColor = Color.FromArgb(0xB4, 0x1E, 0x1E);

        private readonly BundleFile _bundle;
        private readonly TextBox _gameBox = new TextBox();
        private readonly Button _browse = new Button();
        private readonly Label _gameStatus = new Label();
        private readonly ListView _list = new ListView();
        private readonly TextBox _details = new TextBox();
        private readonly Label _blocked = new Label();
        private readonly LinkLabel _checkAll = new LinkLabel();
        private readonly LinkLabel _checkNone = new LinkLabel();
        private readonly Button _install = new Button();
        private readonly Button _close = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly Timer _gameWatch = new Timer();

        private GameInfo _game;
        private string _blockedReason;
        private bool _filling;
        private bool _installing;

        /// <summary>Создаёт окно.</summary>
        /// <param name="bundle">Сборка.</param>
        /// <param name="gameRoot">Папка игры из ключа <c>--game</c>; <c>null</c> — искать самому.</param>
        public MainForm(BundleFile bundle, string gameRoot)
        {
            _bundle = bundle;

            Program.Trace("form: layout");
            BuildLayout();
            Program.Trace("form: fill");
            FillList();

            bool detect = string.IsNullOrEmpty(gameRoot);
            Program.Trace("form: game " + (detect ? "detect" : gameRoot));
            ApplyGameRoot(detect ? GameLocator.FindCandidates().FirstOrDefault() : GameLocator.FindRootFrom(gameRoot), detect);
            Program.Trace("form: blocked");
            RefreshBlocked();
            Program.Trace("form: ready");

            _gameWatch.Interval = 1500;
            _gameWatch.Tick += (s, e) => RefreshBlocked();
        }

        /// <summary>
        /// Рисует окно в PNG и закрывает его — служебный режим <c>--render</c>: так вёрстку можно
        /// проверить без человека у экрана. Окно показывается за пределами экрана.
        /// </summary>
        /// <param name="bundle">Сборка.</param>
        /// <param name="gameRoot">Папка игры либо <c>null</c>.</param>
        /// <param name="pngPath">Куда сохранить картинку.</param>
        /// <returns>Код возврата.</returns>
        public static int Render(BundleFile bundle, string gameRoot, string pngPath)
        {
            using (var form = new MainForm(bundle, gameRoot))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                Program.Trace("render: show");
                form.Show();
                Program.Trace("render: doevents");
                Application.DoEvents();

                Program.Trace("render: draw");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    Program.Trace("render: save");
                    bitmap.Save(pngPath, ImageFormat.Png);
                }

                Program.Trace("render: close");
                form.Close();
            }

            Program.Trace("render: done");

            return Program.ExitOk;
        }

        /// <inheritdoc />
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _gameWatch.Start();
            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
            }

            ActiveControl = _list;
            UpdateInstallButton();
        }

        /// <inheritdoc />
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Посреди установки окно не закрывается: прерванная на полпути установка хуже любой.
            if (_installing)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gameWatch.Stop();
                _gameWatch.Dispose();
            }

            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ вёрстка

        private void BuildLayout()
        {
            BundleManifest m = _bundle.Manifest;

            SuspendLayout();
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = m.Name + " " + m.Version + " — установка модов";
            ClientSize = new Size(700, 640);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (ArgumentException)
            {
                // без иконки окно всё равно работает
            }

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(14, 12, 14, 12),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var title = new Label
            {
                Text = m.Name,
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size * 1.5F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 2),
            };

            var subtitle = new Label
            {
                Text = Subtitle(m),
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 10),
            };

            var gameLabel = new Label { Text = "Папка игры:", AutoSize = true, Margin = new Padding(0, 0, 0, 3) };

            _gameBox.Dock = DockStyle.Fill;
            _gameBox.Margin = new Padding(0, 1, 6, 0);
            _gameBox.KeyDown += OnGameBoxKeyDown;
            _gameBox.Leave += (s, e) => ApplyGameRootIfChanged();

            _browse.Text = "Обзор…";
            _browse.AutoSize = true;
            _browse.Margin = new Padding(0);
            _browse.Click += OnBrowseClick;

            var gameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            gameRow.Controls.Add(_gameBox, 0, 0);
            gameRow.Controls.Add(_browse, 1, 0);

            _gameStatus.AutoSize = true;
            _gameStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _gameStatus.Margin = new Padding(0, 4, 0, 10);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.MultiSelect = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.Margin = new Padding(0, 0, 0, 6);
            _list.Columns.Add("Мод");
            _list.Columns.Add("Версия");
            _list.Columns.Add("В игре сейчас");
            _list.ItemChecked += (s, e) => { if (!_filling) UpdateInstallButton(); };
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _list.Resize += (s, e) => FitColumns();

            _details.Dock = DockStyle.Fill;
            _details.Multiline = true;
            _details.ReadOnly = true;
            _details.ScrollBars = ScrollBars.Vertical;
            _details.Height = 66;
            _details.Margin = new Padding(0, 0, 0, 6);
            _details.BackColor = SystemColors.Control;

            _blocked.AutoSize = true;
            _blocked.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _blocked.ForeColor = DangerColor;
            _blocked.Font = new Font(Font, FontStyle.Bold);
            _blocked.Margin = new Padding(0, 0, 0, 6);
            _blocked.Visible = false;

            _checkAll.Text = "отметить все";
            _checkAll.AutoSize = true;
            _checkAll.Margin = new Padding(0, 6, 12, 0);
            _checkAll.LinkClicked += (s, e) => SetAllChecked(true);

            _checkNone.Text = "снять все";
            _checkNone.AutoSize = true;
            _checkNone.Margin = new Padding(0, 6, 0, 0);
            _checkNone.LinkClicked += (s, e) => SetAllChecked(false);

            var links = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            links.Controls.Add(_checkAll);
            links.Controls.Add(_checkNone);

            _install.Text = "Установить";
            _install.AutoSize = true;
            _install.MinimumSize = new Size(130, 30);
            _install.Margin = new Padding(0, 0, 8, 0);
            _install.Click += OnInstallClick;

            _close.Text = "Закрыть";
            _close.AutoSize = true;
            _close.MinimumSize = new Size(90, 30);
            _close.Margin = new Padding(0);
            _close.Click += (s, e) => Close();

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                WrapContents = false,
            };
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_install);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(links, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Height = 96;
            _log.Margin = new Padding(0);
            _log.Text =
                "Отметьте моды и нажмите «Установить»." + Environment.NewLine +
                "Прежние версии отмеченных модов будут выключены (переименованы в .mtmod.off), а не удалены. " +
                "Чужие моды установщик не трогает.";

            AddRow(root, title, SizeType.AutoSize);
            AddRow(root, subtitle, SizeType.AutoSize);
            AddRow(root, gameLabel, SizeType.AutoSize);
            AddRow(root, gameRow, SizeType.AutoSize);
            AddRow(root, _gameStatus, SizeType.AutoSize);
            AddRow(root, _list, SizeType.Percent);
            AddRow(root, _details, SizeType.AutoSize);
            AddRow(root, _blocked, SizeType.AutoSize);
            AddRow(root, bottom, SizeType.AutoSize);
            AddRow(root, _log, SizeType.AutoSize);

            Controls.Add(root);
            AcceptButton = _install;
            CancelButton = _close;
            ResumeLayout(true);
        }

        private static void AddRow(TableLayoutPanel table, Control control, SizeType size)
        {
            table.RowStyles.Add(size == SizeType.Percent ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(size));
            table.Controls.Add(control, 0, table.RowCount);
            table.RowCount++;
        }

        private static string Subtitle(BundleManifest m)
        {
            var text = new StringBuilder();
            text.Append("Версия сборки ").Append(m.Version);

            DateTime created;
            if (DateTime.TryParse(m.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out created))
            {
                text.Append(" · собрана ").Append(created.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(m.GameVersion))
            {
                text.Append(" · для клиента ").Append(m.GameVersion);
            }

            text.Append(". Отметьте моды, которые нужно поставить в игру.");
            return text.ToString();
        }

        private void FitColumns()
        {
            if (_list.Columns.Count < 3)
            {
                return;
            }

            float scale = DeviceDpi / 96F;
            int version = (int)(80 * scale);
            int status = (int)(200 * scale);
            int name = _list.ClientSize.Width - version - status - SystemInformation.VerticalScrollBarWidth;
            _list.Columns[0].Width = Math.Max((int)(140 * scale), name);
            _list.Columns[1].Width = version;
            _list.Columns[2].Width = status;
        }

        // ------------------------------------------------------------------ список

        private void FillList()
        {
            _filling = true;
            try
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (BundleComponent c in _bundle.Manifest.Components)
                {
                    var item = new ListViewItem(c.Title) { Tag = c, Checked = true };
                    item.SubItems.Add(c.Version ?? string.Empty);
                    item.SubItems.Add("—");
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
                _filling = false;
            }

            FitColumns();
        }

        /// <summary>Обновляет колонку «В игре сейчас».</summary>
        /// <param name="applyDefaults">
        /// Расставить галочки заново — при смене папки игры: без галочки остаются моды, которые
        /// ставить незачем (в игре уже эта сборка или версия новее). После установки галочки не
        /// трогаются: их только что расставил человек.
        /// </param>
        private void RefreshStatuses(bool applyDefaults)
        {
            InstalledMods installed = _game != null && _game.IsUsable ? InstalledMods.Scan(_game.ModsDir) : null;

            _filling = true;
            try
            {
                foreach (ListViewItem item in _list.Items)
                {
                    var c = (BundleComponent)item.Tag;
                    bool nothingToDo = false;
                    item.SubItems[2].Text = installed == null ? "—" : installed.Describe(c, out nothingToDo);
                    if (applyDefaults)
                    {
                        item.Checked = !nothingToDo;
                    }
                }
            }
            finally
            {
                _filling = false;
            }

            UpdateInstallButton();
        }

        private void ShowDetails()
        {
            ListViewItem item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
            if (item == null)
            {
                _details.Text = string.Empty;
                return;
            }

            var c = (BundleComponent)item.Tag;
            string description = string.IsNullOrWhiteSpace(c.Description) ? "Описания нет." : c.Description.Trim();
            _details.Text = description + Environment.NewLine + Environment.NewLine + c.Id + " · " + c.FileName;
        }

        private void SetAllChecked(bool value)
        {
            _filling = true;
            try
            {
                foreach (ListViewItem item in _list.Items)
                {
                    item.Checked = value;
                }
            }
            finally
            {
                _filling = false;
            }

            UpdateInstallButton();
        }

        private List<BundleComponent> CheckedComponents()
        {
            return _list.Items.Cast<ListViewItem>()
                .Where(i => i != null && i.Checked)
                .Select(i => (BundleComponent)i.Tag)
                .ToList();
        }

        // ------------------------------------------------------------------ папка игры

        private void OnGameBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ApplyGameRootIfChanged();
            }
        }

        private void ApplyGameRootIfChanged()
        {
            string typed = _gameBox.Text.Trim();
            string current = _game == null ? string.Empty : _game.Root;
            if (!string.Equals(typed, current, StringComparison.OrdinalIgnoreCase))
            {
                ApplyGameRoot(typed.Length == 0 ? null : GameLocator.FindRootFrom(typed), false);
            }
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Папка игры «Мир танков» — та, где лежат paths.xml и WorldOfTanks.exe.";
                dialog.ShowNewFolderButton = false;
                if (_game != null && !string.IsNullOrEmpty(_game.Root))
                {
                    dialog.SelectedPath = _game.Root;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyGameRoot(GameLocator.FindRootFrom(dialog.SelectedPath), false);
                }
            }
        }

        private void ApplyGameRoot(string root, bool detected)
        {
            _game = string.IsNullOrEmpty(root) ? null : GameLocator.Inspect(root);
            _gameBox.Text = _game != null ? _game.Root : string.Empty;

            if (_game == null)
            {
                SetGameStatus(
                    detected
                        ? "Игра не найдена автоматически. Укажите её папку кнопкой «Обзор…»."
                        : "Укажите папку игры.",
                    true);
            }
            else if (!_game.IsUsable)
            {
                SetGameStatus(_game.Problem, true);
            }
            else
            {
                string text = "Моды встанут в " + _game.ModsDir + (detected ? " (папка найдена автоматически)" : string.Empty) + ".";
                string mismatch = Program.VersionMismatch(_bundle.Manifest, _game);
                SetGameStatus(mismatch == null ? text : text + Environment.NewLine + "Внимание: " + mismatch, false);
            }

            RefreshStatuses(true);
        }

        private void SetGameStatus(string text, bool problem)
        {
            _gameStatus.Text = text;
            _gameStatus.ForeColor = problem ? DangerColor : SystemColors.GrayText;
        }

        // ------------------------------------------------------------------ установка

        private void RefreshBlocked()
        {
            Program.Trace("blocked: check");
            _blockedReason = GameProcess.WhyBlocked();
            Program.Trace("blocked: " + (_blockedReason ?? "no"));
            _blocked.Text = _blockedReason ?? string.Empty;
            _blocked.Visible = _blockedReason != null;
            UpdateInstallButton();
        }

        private void UpdateInstallButton()
        {
            // i != null — не перестраховка: пока создаётся окно, ListView переносит строки в
            // системный элемент и шлёт ItemChecked на каждую, а перечисление в этот момент
            // отдаёт ещё не перенесённые строки как null. Итог пересчитывается в OnShown.
            int count = _list.Items.Cast<ListViewItem>().Count(i => i != null && i.Checked);
            _install.Text = count > 0 ? "Установить (" + count + ")" : "Установить";
            _install.Enabled = !_installing && count > 0 && _game != null && _game.IsUsable && _blockedReason == null;
            _close.Enabled = !_installing;
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            List<BundleComponent> selected = CheckedComponents();
            if (selected.Count == 0 || _game == null || !_game.IsUsable)
            {
                return;
            }

            string blocked = GameProcess.WhyBlocked();
            if (blocked != null)
            {
                MessageBox.Show(this, blocked, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshBlocked();
                return;
            }

            var question = new StringBuilder();
            question.Append("Поставить в игру ").Append(selected.Count).Append(' ')
                .Append(Program.Plural(selected.Count, "мод", "мода", "модов")).AppendLine(":");
            foreach (BundleComponent c in selected)
            {
                question.Append("   • ").Append(c.Title).Append(' ').AppendLine(c.Version);
            }

            question.AppendLine().Append("Куда: ").AppendLine(_game.ModsDir).AppendLine();
            question.AppendLine("Прежние версии этих модов будут выключены (переименованы в .mtmod.off), а не удалены. Чужие моды не трогаются.");

            if (MessageBox.Show(this, question.ToString(), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _installing = true;
            UseWaitCursor = true;
            UpdateInstallButton();
            _log.Clear();

            List<InstallResult> results;
            try
            {
                results = new ModInstaller(_bundle, _game.ModsDir).InstallAll(selected, AppendLog);
            }
            catch (Exception ex)
            {
                AppendLog("Сбой установки: " + ex.Message);
                results = null;
            }
            finally
            {
                _installing = false;
                UseWaitCursor = false;
            }

            RefreshStatuses(false);

            if (results == null)
            {
                MessageBox.Show(this, "Установка прервана сбоем — подробности в журнале внизу окна.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int ok = results.Count(r => r.Success);
            if (ok == results.Count)
            {
                AppendLog("Готово. Изменения вступят в силу при следующем запуске игры.");
                MessageBox.Show(this,
                    "Готово: установлено " + ok + " " + Program.Plural(ok, "мод", "мода", "модов") +
                    ". Изменения вступят в силу при следующем запуске игры.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this,
                    "Установлено " + ok + " из " + results.Count + ". Что помешало — в журнале внизу окна.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AppendLog(string line)
        {
            _log.AppendText(line + Environment.NewLine);
        }
    }
}
