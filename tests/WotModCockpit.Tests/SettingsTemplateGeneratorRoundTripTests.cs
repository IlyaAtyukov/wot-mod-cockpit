using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Стык модели и кодогенератора: то, что кокпит НАПИСАЛ, кокпит обязан ПРОЧИТАТЬ обратно.
///
/// <para>Это главный сценарий импорта из <c>.py</c>: пользователь собрал форму, кокпит положил
/// в проект <c>settings_schema.py</c>, через неделю пользователь открывает дизайнер снова —
/// и должен увидеть ровно ту же форму, а не «не удалось разобрать». Разобрать словарь из
/// произвольного кода нельзя, но СВОЙ собственный вывод импортёр обязан читать всегда.</para>
/// </summary>
public class SettingsTemplateGeneratorRoundTripTests
{
    /// <summary>Сгенерированный модуль читается обратно в тот же самый шаблон.</summary>
    [Fact]
    public void GeneratedSchema_IsReadBackIdentically()
    {
        SettingsTemplate original = SettingsTemplateModelTests.FullTemplate();

        string code = SettingsCodeGenerator.Generate(original).SchemaModuleCode;
        TemplateImportResult back = TemplateImporter.FromPython(code);

        Assert.True(back.Success, back.Describe());
        Assert.Equal(TemplateImportSource.PythonLiteral, back.Source);
        Assert.Equal(TemplateJson.ToCanonicalJson(original), TemplateJson.ToCanonicalJson(back.Template!));
    }

    /// <summary>
    /// То же самое на чужих шаблонах: каждый из 11 шаблонов фикстуры (они повторяют структуру
    /// реальных модов) прогоняется через генератор и обратно через импортёр. Такой круг ловит
    /// любое расхождение между печатью питона и разбором — включая мелочи вроде 3 против 3.0
    /// и мёртвого itemRenderer.
    /// </summary>
    [Fact]
    public void EverySampleTemplate_SurvivesGenerateThenImport()
    {
        var failures = new List<string>();

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate original = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;

            // Чужие моды хранятся под ключами вроде 'ru.example.ui-scale', а генератор пишет
            // LINKAGE в код — приводим к тому виду, который кокпит порождает сам.
            original.Linkage = "ru.local." + linkage.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
            original.SettingsVersion ??= 1;

            string code = SettingsCodeGenerator.Generate(original).SchemaModuleCode;
            TemplateImportResult back = TemplateImporter.FromPython(code);

            if (!back.Success)
            {
                failures.Add($"{linkage}: {back.Describe()}");
                continue;
            }

            string expected = TemplateJson.ToCanonicalJson(original);
            string actual = TemplateJson.ToCanonicalJson(back.Template!);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add($"{linkage}:{Environment.NewLine}  было:  {expected}{Environment.NewLine}  стало: {actual}");
        }

        Assert.True(failures.Count == 0,
            $"расхождений {failures.Count} из {SettingsTestData.Linkages.Count}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>Значения в сгенерированном коде подставляются из DEFAULTS, а не теряются.</summary>
    [Fact]
    public void GeneratedSchema_KeepsDefaultValues()
    {
        SettingsTemplate original = SettingsTemplateModelTests.FullTemplate();
        SettingsTemplate back = TemplateImporter
            .FromPython(SettingsCodeGenerator.Generate(original).SchemaModuleCode).Template!;

        Assert.Equal("ru.local.hello_tanker", back.Linkage);
        Assert.Equal(3, back.SettingsVersion);
        Assert.True(back.FindByVarName("showGreeting")!.Value!.AsBool);
        Assert.Equal("Привет, танкист!", back.FindByVarName("greetingText")!.Value!.AsString);
        Assert.Equal(new[] { 61, HotKeyValue.Ctrl },
            HotKeyValue.Flatten(back.FindByVarName("toggleKey")!.Value));
    }
}
