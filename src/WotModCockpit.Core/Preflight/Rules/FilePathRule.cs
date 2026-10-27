namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT016. Файловая операция по относительному пути.
///
/// <para><b>Последствие.</b> Клиент стартует из корня игры, но полагаться на cwd нельзя — он
/// зависит от способа запуска (лаунчер, ярлык, отладчик). Относительный путь в <c>open()</c>
/// или <c>os.makedirs</c> кладёт файл неизвестно куда. Реальный пример из корпуса:
/// <c>me.poliroid.mtreplays.mtrmanager/_constants.py:15</c> объявляет
/// <c>REPLAY_FLAG_FILE = 'replay_need_to_play.killme'</c> и пишет его через
/// <c>open(REPLAY_FLAG_FILE, 'w')</c> — файл ложится в текущий каталог процесса.</para>
///
/// <para>Правильные образцы из того же корпуса: <c>battlehits</c> и <c>modsSettingsApi</c> строят
/// путь от <c>os.path.dirname(prefsFilePath)</c>, XVM — от <c>res_mods/configs/xvm</c>,
/// эталонный шаблон — от корня игры, найденного перебором кандидатов
/// (<c>os.getcwd()</c>, <c>ResMgr.appDirectory()</c> и его родитель) с проверкой наличия
/// подкаталога <c>mods/</c>.</para>
/// </summary>
public sealed class FilePathRule : PerFileRule
{
    private static readonly string[] WriteCalls =
    {
        "open", "io.open", "os.makedirs", "os.mkdir", "os.remove", "os.unlink", "os.rename", "shelve.open"
    };

    /// <summary>Имена, которые считаются «корнем», привязанным к установке игры.</summary>
    private static readonly string[] AnchorNames =
    {
        "getcwd", "appDirectory", "prefsFilePath", "PATH_ROOT", "_game_root", "game_root",
        "dirname", "GAME_ROOT", "ROOT", "abspath", "expanduser"
    };

    /// <inheritdoc/>
    public override string Id => "WOT016";

    /// <inheritdoc/>
    public override string Title => "Файловая операция по относительному пути";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        Dictionary<string, PythonStatement> constants = PathConstants(context);

        // Одна константа-путь используется по десятку раз в файле (в mtrmanager DB_FILENAME —
        // 13 раз). Причина одна, поэтому и сообщение одно: иначе список Problems забьётся
        // повторами одного и того же дефекта.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;

            foreach (string call in WriteCalls)
            {
                int open = PreflightSyntax.FindCall(code, call, allowMethodReceiver: call.Contains('.'));
                if (open < 0) continue;

                // Значения литералов берём из RawCode: в Code содержимое строк вырезано,
                // и режим 'w' с именем файла там уже не прочитать.
                IReadOnlyList<string> args = PreflightSyntax.SplitArgumentsRaw(code, st.RawCode, open);
                if (args.Count == 0) continue;

                // open() без режима записи — чтение; мусора в корне игры оно не создаёт.
                if (call is "open" or "io.open" && args.Count >= 2 && !IsWriteMode(args[1])) continue;
                if (call is "open" or "io.open" && args.Count == 1) continue;

                string target = args[0].Trim();
                if (IsQuoted(target))
                {
                    string literal = target[1..^1];
                    if (literal.Length == 0 || IsAbsolute(literal)) continue;
                    if (reported.Add(literal)) Report(file, st, output, literal, relative: true);
                    break;                                       // одна находка на оператор
                }

                // Путь пришёл переменной. Резолвим ТОЛЬКО ALL_CAPS-константы уровня модуля:
                // константа пути обычно объявлена в _constants.py и используется в другом файле,
                // а вот обычное имя (`path`, `name`) по всему дереву резолвить нельзя — это
                // почти всегда параметр функции. Замерено на XVM: без этого ограничения
                // 23 ложных срабатывания в вендорной библиотеке fs/, с ним — ноль.
                if (!IsPlainName(target) || !IsUpperSnake(target)) continue;
                if (!constants.TryGetValue(target, out PythonStatement? decl)) continue;
                if (IsAnchored(decl.Trimmed)) continue;

                if (reported.Add(target)) Report(file, st, output, target, relative: false, declaredAt: decl.Line);
                break;                                           // одна находка на оператор
            }
        }
    }

    private void Report(ModSourceFile file, PythonStatement st, ICollection<PreflightDiagnostic> output,
        string what, bool relative, int declaredAt = 0)
    {
        string message = relative
            ? $"Файл открывается на запись по ОТНОСИТЕЛЬНОМУ пути «{what}». Клиент стартует из корня игры, " +
              "но текущий каталог зависит от способа запуска (лаунчер, ярлык, отладчик), поэтому файл ляжет " +
              "неизвестно куда — обычно мусором в корень установки."
            : $"Путь {what} (объявлен на строке {declaredAt}) собран без привязки к корню игры: " +
              "ни os.getcwd(), ни ResMgr.appDirectory(), ни каталог preferences.xml в цепочке не участвуют. " +
              "Куда именно ляжет файл, зависит от текущего каталога процесса.";

        output.Add(At(file, st.Line, PreflightSeverity.Warning, message,
            "Стройте путь от найденного корня игры: перебирайте os.getcwd(), ResMgr.appDirectory() и его " +
            "родителя, выбирая тот, где есть подкаталог mods/, и складывайте конфиг в " +
            "mods/configs/<имя мода>/ через os.path.join.",
            "me.poliroid.mtreplays.mtrmanager/_constants.py:15 (как НЕ надо)"));
    }

    /// <summary>Константы-пути, объявленные где угодно в дереве мода: они часто живут в <c>_constants.py</c>.</summary>
    private static Dictionary<string, PythonStatement> PathConstants(PreflightContext context)
    {
        var map = new Dictionary<string, PythonStatement>(StringComparer.Ordinal);
        foreach (ModSourceFile f in context.Files)
        {
            foreach (PythonStatement st in f.Model.Statements)
            {
                if (!st.IsModuleLevel) continue;
                int eq = PreflightSyntax.IndexOfAssignment(st.Trimmed);
                if (eq <= 0) continue;
                string name = st.Trimmed[..eq].Trim();
                if (!IsPlainName(name) || !IsUpperSnake(name)) continue;
                map.TryAdd(name, st);
            }
        }
        return map;
    }

    private static bool IsAnchored(string declaration)
    {
        foreach (string anchor in AnchorNames)
            if (declaration.Contains(anchor, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsWriteMode(string arg)
    {
        string a = arg.Trim();
        if (!IsQuoted(a)) return true;                 // режим вычисляется — считаем записью
        string mode = a[1..^1];
        return mode.Contains('w', StringComparison.Ordinal)
            || mode.Contains('a', StringComparison.Ordinal)
            || mode.Contains('+', StringComparison.Ordinal);
    }

    private static bool IsQuoted(string s) =>
        s.Length >= 2 && (s[0] is '\'' or '"') && s[^1] == s[0];

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || (path.Length > 1 && path[1] == ':');

    /// <summary>Имя выглядит константой: <c>REPLAY_FLAG_FILE</c>, <c>CONFIG_PATH</c>.</summary>
    private static bool IsUpperSnake(string s)
    {
        bool letter = false;
        foreach (char c in s)
        {
            if (char.IsLower(c)) return false;
            if (char.IsLetter(c)) letter = true;
        }
        return letter;
    }

    private static bool IsPlainName(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }
}
