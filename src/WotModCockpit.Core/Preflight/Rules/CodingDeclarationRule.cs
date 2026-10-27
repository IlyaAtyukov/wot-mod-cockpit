namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT015. Не-ASCII в исходнике без строки <c># -*- coding: utf-8 -*-</c>.
///
/// <para><b>Последствие.</b> Тело мода едет в <c>.mtmod</c> обычным <c>.py</c> и компилируется
/// УЖЕ ВНУТРИ КЛИЕНТА. Python 2.7 по PEP 263 отказывается компилировать файл с байтом
/// &gt;= 0x80 без объявления кодировки: <c>SyntaxError: Non-ASCII character</c>. Мод молча мёртв,
/// в логе одна строка.</para>
///
/// <para><b>Это не дублирование существующего гейта.</b> Разведка ПРОВЕРИЛА ЗАПУСКОМ:
/// <c>tools/check_py2_syntax.py</c> на файле из двух строк с кириллицей и без coding-строки
/// ответил <c>[OK]</c> и вернул 0 — он читает файл как utf-8 и парсит парсером Python 3,
/// у которого utf-8 по умолчанию. То есть это реальная дыра, которую закрывает только preflight.</para>
/// </summary>
public sealed class CodingDeclarationRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT015";

    /// <inheritdoc/>
    public override string Title => "Не-ASCII без объявления кодировки";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        if (file.Model.HasNonAscii && !file.Model.HasCodingDeclaration)
        {
            output.Add(At(file, FirstNonAsciiLine(file), PreflightSeverity.Error,
                "В файле есть символы вне ASCII, а объявления кодировки в первых двух строках нет. " +
                "Этот файл компилируется УЖЕ ВНУТРИ КЛИЕНТА, и Python 2.7 по PEP 263 откажется его " +
                "компилировать: SyntaxError: Non-ASCII character — мод молча не загрузится. " +
                "Обратите внимание: tools/check_py2_syntax.py этого НЕ ловит (проверено запуском), " +
                "потому что парсит файл парсером Python 3.",
                "Добавьте первой строкой: # -*- coding: utf-8 -*-",
                "PEP 263; scripts/common/bw_site.py:7 (DEFAULT_ENCODING = 'utf-8')"));
        }

        if (file.HasBom)
        {
            output.Add(At(file, 1, PreflightSeverity.Warning,
                "Файл начинается с UTF-8 BOM. Python 2.7 терпит BOM в начале .py, но любой инструмент, " +
                "который читает файл побайтово (в том числе компиляция внутри клиента через compile()), " +
                "может споткнуться о лишние три байта.",
                "Сохраните файл в UTF-8 без BOM."));
        }
    }

    private static int FirstNonAsciiLine(ModSourceFile file)
    {
        foreach (PythonPhysicalLine line in file.Model.Lines)
            foreach (char c in line.Raw)
                if (c > 127) return line.Number;
        return 1;
    }
}
