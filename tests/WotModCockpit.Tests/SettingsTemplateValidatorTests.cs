using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Валидатор шаблона. Каждое правило — отдельный тест, потому что каждое правило описывает
/// ОТДЕЛЬНУЮ поломку у игрока: сам ModsSettingsAPI не проверяет ничего и принимает любой
/// словарь, а последствия видны только в игре.
/// </summary>
public class SettingsTemplateValidatorTests
{
    // ------------------------------------------------------------------ шапка

    /// <summary>Правильный шаблон не даёт ни ошибок, ни предупреждений.</summary>
    [Fact]
    public void GoodTemplate_IsClean()
    {
        TemplateValidationResult result = TemplateValidator.Validate(Good());

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
    }

    /// <summary>Пустой linkage — ошибка: настройки некуда сохранять.</summary>
    [Fact]
    public void EmptyLinkage_IsError()
    {
        SettingsTemplate template = Good();
        template.Linkage = string.Empty;

        AssertError(template, TemplateRules.BadLinkage);
    }

    /// <summary>
    /// Не-ASCII в linkage — ошибка: sorted(templates, key=str.lower) в Python 2 бросит
    /// TypeError на unicode, и окно не откроется ни у одного мода.
    /// </summary>
    [Theory]
    [InlineData("ру.локальный.мод")]
    [InlineData("my mod")]
    [InlineData("мод")]
    public void NonAsciiLinkage_IsError(string linkage)
    {
        SettingsTemplate template = Good();
        template.Linkage = linkage;

        AssertError(template, TemplateRules.BadLinkage);
    }

    /// <summary>Пустое имя мода — ошибка.</summary>
    [Fact]
    public void EmptyDisplayName_IsError()
    {
        SettingsTemplate template = Good();
        template.ModDisplayName = "   ";

        AssertError(template, TemplateRules.EmptyDisplayName);
    }

    /// <summary>Слишком длинное имя мода — предупреждение.</summary>
    [Fact]
    public void LongDisplayName_IsWarning()
    {
        SettingsTemplate template = Good();
        template.ModDisplayName = new string('я', TemplateLimits.MaxDisplayNameLength + 1);

        AssertWarning(template, TemplateRules.LongDisplayName);
    }

    /// <summary>
    /// Отсутствие settingsVersion — предупреждение, а не ошибка: так живёт большинство реальных
    /// модов (в фикстуре — 8 шаблонов из 11).
    /// </summary>
    [Fact]
    public void NoSettingsVersion_IsWarning()
    {
        SettingsTemplate template = Good();
        template.SettingsVersion = null;

        AssertWarning(template, TemplateRules.NoSettingsVersion);
    }

    /// <summary>Отрицательная версия — ошибка.</summary>
    [Fact]
    public void NegativeSettingsVersion_IsError()
    {
        SettingsTemplate template = Good();
        template.SettingsVersion = -1;

        AssertError(template, TemplateRules.BadSettingsVersion);
    }

    /// <summary>Форма без единого контрола — ошибка.</summary>
    [Fact]
    public void EmptyForm_IsError()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };

        AssertError(template, TemplateRules.EmptyForm);
    }

    /// <summary>
    /// Форма из ОДНОГО тумблера — не ошибка, а предупреждение. Мод «включить/выключить
    /// галочкой» законен: <c>api.getSettingsFromTemplate</c> читает <c>enabled</c> ДО колонок,
    /// и окно рисует блок мода с тумблером. Ошибкой это было раньше, и такой мод просто
    /// не генерировался.
    /// </summary>
    [Fact]
    public void EmptyForm_WithEnabledToggle_IsOnlyWarning()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = true };

        AssertWarning(template, TemplateRules.EmptyForm);
        Assert.False(TemplateValidator.Validate(template).HasErrors);

        // И такая форма действительно даёт настройку: тумблер попадает в словарь значений.
        Assert.Equal(new[] { TemplateJson.EnabledKey }, template.BuildDefaults().Select(p => p.Key));
    }

    /// <summary>
    /// Посторонний ключ верхнего уровня — предупреждение (так бывает у реальных модов; в фикстуре
    /// это 'random' у ru.example.paint).
    /// </summary>
    [Fact]
    public void ExtraTemplateKey_IsWarning()
    {
        SettingsTemplate template = Good();
        template.SetExtra("random", SettingValue.Of(52817L));

        AssertWarning(template, TemplateRules.ExtraTemplateKey);
    }

    // ------------------------------------------------------------------ имена и подписи

    /// <summary>Два контрола с одним varName — ошибка: в настройках останется одно значение.</summary>
    [Fact]
    public void DuplicateVarName_IsError()
    {
        SettingsTemplate template = Good();
        template.Column2.Add(SettingControl.CheckBox("Другая галка", "showGreeting", false));

        TemplateValidationResult result = TemplateValidator.Validate(template);
        TemplateIssue issue = Assert.Single(result.Issues, i => i.Code == TemplateRules.DuplicateVarName);
        Assert.Equal(TemplateIssueSeverity.Error, issue.Severity);
        Assert.Contains("column1[1]", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>Дубликат ищется по ОБЕИМ колонкам, а не внутри одной.</summary>
    [Fact]
    public void DuplicateVarName_IsFoundAcrossColumns()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        template.Column1.Add(SettingControl.CheckBox("A", "same", true));
        template.Column2.Add(SettingControl.CheckBox("B", "same", false));

        AssertError(template, TemplateRules.DuplicateVarName);
    }

    /// <summary>varName 'enabled' занят самим API.</summary>
    [Fact]
    public void ReservedVarName_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1[1].VarName = "enabled";

        AssertError(template, TemplateRules.ReservedVarName);
    }

    /// <summary>varName обязан быть ASCII-идентификатором: это ключ словаря Python 2.</summary>
    [Theory]
    [InlineData("не идентификатор")]
    [InlineData("2fast")]
    [InlineData("with-dash")]
    [InlineData("имя")]
    public void BadVarName_IsError(string varName)
    {
        SettingsTemplate template = Good();
        template.Column1[1].VarName = varName;

        AssertError(template, TemplateRules.BadVarName);
    }

    /// <summary>Контрол без varName в настройки не попадёт.</summary>
    [Fact]
    public void MissingVarName_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1[1].VarName = null;

        AssertError(template, TemplateRules.MissingVarName);
    }

    /// <summary>Пустая подпись — ошибка: игрок не поймёт, что это за настройка.</summary>
    [Fact]
    public void EmptyText_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Text = "  ";

        AssertError(template, TemplateRules.EmptyText);
    }

    /// <summary>Слишком длинная подпись — предупреждение: не влезет в колонку.</summary>
    [Fact]
    public void LongText_IsWarning()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Text = new string('ы', TemplateLimits.MaxTextLength + 1);

        AssertWarning(template, TemplateRules.LongText);
    }

    /// <summary>У подписи не может быть значения: «Применить» сломается.</summary>
    [Fact]
    public void LabelWithVarName_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1[0].VarName = "header";
        template.Column1[0].Value = SettingValue.Of(1L);

        AssertError(template, TemplateRules.LabelWithValue);
    }

    /// <summary>Контрол без значения не попадёт в словарь настроек.</summary>
    [Fact]
    public void MissingValue_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Value = null;

        AssertError(template, TemplateRules.MissingValue);
    }

    // ------------------------------------------------------------------ списки

    /// <summary>Dropdown без вариантов — ошибка.</summary>
    [Fact]
    public void DropdownWithoutOptions_IsError()
    {
        SettingsTemplate template = Good();
        SettingControl dropdown = template.FindByVarName("position")!;
        dropdown.Options.Clear();

        AssertError(template, TemplateRules.NoOptions);
    }

    /// <summary>RadioButtonGroup без вариантов — ошибка.</summary>
    [Fact]
    public void RadioGroupWithoutOptions_IsError()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        template.Column1.Add(SettingControl.RadioButtonGroup("Способ", "way", 0));

        AssertError(template, TemplateRules.NoOptions);
    }

    /// <summary>options у типа, который их не читает, — предупреждение.</summary>
    [Fact]
    public void OptionsOnCheckBox_IsWarning()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Options.Add(new ControlOption("Вариант"));

        AssertWarning(template, TemplateRules.OptionsNotSupported);
    }

    /// <summary>value — это ИНДЕКС в options, и он обязан быть внутри списка.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void OptionIndexOutOfRange_IsError(int index)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!.Value = SettingValue.Of((long)index);

        AssertError(template, TemplateRules.OptionIndexOutOfRange);
    }

    /// <summary>Крайние индексы допустимы.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void OptionIndexAtBounds_IsAccepted(int index)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!.Value = SettingValue.Of((long)index);

        Assert.False(TemplateValidator.Validate(template).Has(TemplateRules.OptionIndexOutOfRange));
    }

    /// <summary>Вариант без подписи — предупреждение.</summary>
    [Fact]
    public void EmptyOptionLabel_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!.Options[1].Label = string.Empty;

        AssertWarning(template, TemplateRules.EmptyOptionLabel);
    }

    /// <summary>Больше девяти вариантов — появится прокрутка (SCROLL_ITEM_LIMIT = 9).</summary>
    [Fact]
    public void ManyDropdownOptions_IsInfo()
    {
        SettingsTemplate template = Good();
        SettingControl dropdown = template.FindByVarName("position")!;
        for (int i = 0; i < 8; i++) dropdown.Options.Add(new ControlOption($"Вариант {i}"));

        TemplateValidationResult result = TemplateValidator.Validate(template);
        Assert.Contains(result.Issues, i =>
            i.Code == TemplateRules.DropdownScroll && i.Severity == TemplateIssueSeverity.Info);
    }

    /// <summary>Ровно девять вариантов прокрутки ещё не дают.</summary>
    [Fact]
    public void NineDropdownOptions_AreQuiet()
    {
        SettingsTemplate template = Good();
        SettingControl dropdown = template.FindByVarName("position")!;
        for (int i = 0; i < 6; i++) dropdown.Options.Add(new ControlOption($"Вариант {i}"));

        Assert.Equal(9, dropdown.Options.Count);
        Assert.False(TemplateValidator.Validate(template).Has(TemplateRules.DropdownScroll));
    }

    // ------------------------------------------------------------------ диапазоны

    /// <summary>minimum не меньше maximum — ползунок будет неподвижен.</summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(10, 0)]
    public void BadRange_IsError(int min, int max)
    {
        SettingsTemplate template = Good();
        SettingControl slider = template.FindByVarName("delaySec")!;
        slider.Minimum = SettingValue.Of((long)min);
        slider.Maximum = SettingValue.Of((long)max);
        slider.Value = SettingValue.Of((long)min);

        AssertError(template, TemplateRules.BadRange);
    }

    /// <summary>snapInterval обязан быть положительным.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSnapInterval_IsError(int snap)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("delaySec")!.SnapInterval = SettingValue.Of((long)snap);

        AssertError(template, TemplateRules.BadSnapInterval);
    }

    /// <summary>Шаг больше самого диапазона — доступны только края.</summary>
    [Fact]
    public void CoarseSnapInterval_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("delaySec")!.SnapInterval = SettingValue.Of(50L);

        AssertWarning(template, TemplateRules.CoarseSnapInterval);
    }

    /// <summary>Дробный шаг допустим: у ползунков масштаба он бывает 0.01 (в фикстуре — ru.example.ui-scale).</summary>
    [Fact]
    public void FractionalSnapInterval_IsAccepted()
    {
        SettingsTemplate template = Good();
        SettingControl slider = template.FindByVarName("delaySec")!;
        slider.Minimum = SettingValue.Of(0.8d);
        slider.Maximum = SettingValue.Of(3.0d);
        slider.SnapInterval = SettingValue.Of(0.01d);
        slider.Value = SettingValue.Of(1.5d);

        Assert.False(TemplateValidator.Validate(template).HasErrors);
    }

    /// <summary>Значение вне диапазона — ошибка.</summary>
    [Fact]
    public void ValueOutOfRange_IsError()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("delaySec")!.Value = SettingValue.Of(99L);

        AssertError(template, TemplateRules.ValueOutOfRange);
    }

    /// <summary>Ползунок без границ — ошибка.</summary>
    [Fact]
    public void SliderWithoutRange_IsError()
    {
        SettingsTemplate template = Good();
        SettingControl slider = template.FindByVarName("delaySec")!;
        slider.Minimum = null;
        slider.Maximum = null;

        AssertError(template, TemplateRules.BadRange);
    }

    /// <summary>format без плейсхолдера — метка не будет меняться.</summary>
    [Fact]
    public void FormatWithoutPlaceholder_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("delaySec")!.Format = "секунд";

        AssertWarning(template, TemplateRules.FormatWithoutPlaceholder);
    }

    /// <summary>Форматы обычного вида — плейсхолдер в начале, в конце или посередине — принимаются.</summary>
    [Theory]
    [InlineData("{{value}}")]
    [InlineData("{{value}} сек.")]
    [InlineData("{{value}} мин.")]
    [InlineData("Масштаб: {{value}}")]
    public void TypicalFormats_AreAccepted(string format)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("delaySec")!.Format = format;

        Assert.False(TemplateValidator.Validate(template).Has(TemplateRules.FormatWithoutPlaceholder));
    }

    // ------------------------------------------------------------------ типы значений

    /// <summary>Галочка с не-bool значением — предупреждение: окно всё равно вернёт bool.</summary>
    [Fact]
    public void CheckBoxWithIntValue_IsWarning()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Value = SettingValue.Of(1L);

        AssertWarning(template, TemplateRules.ValueTypeMismatch);
    }

    /// <summary>Строка ввода с числом — ошибка.</summary>
    [Fact]
    public void TextInputWithNumber_IsError()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        SettingControl input = SettingControl.TextInput("Текст", "text", "x");
        input.Value = SettingValue.Of(1L);
        template.Column1.Add(input);

        AssertError(template, TemplateRules.ValueTypeMismatch);
    }

    /// <summary>Цвет обязан быть шестью hex-цифрами без решётки.</summary>
    [Theory]
    [InlineData("#FFCC00")]
    [InlineData("FFC")]
    [InlineData("GGGGGG")]
    [InlineData("")]
    public void BadColor_IsError(string hex)
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        SettingControl color = SettingControl.ColorChoice("Цвет", "markColor", "FFCC00");
        color.Value = SettingValue.Of(hex);
        template.Column1.Add(color);

        AssertError(template, TemplateRules.BadColor);
    }

    /// <summary>Горячая клавиша с запрещённым кодом — ошибка: игрок не сможет её переназначить.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(258)]
    public void ExcludedHotKey_IsError(int code)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("toggleKey")!.Value = HotKeyValue.Create(code);

        AssertError(template, TemplateRules.ExcludedKey);
    }

    /// <summary>Код вне диапазона Keys.py — ошибка.</summary>
    [Theory]
    [InlineData(999)]
    [InlineData(-4)]
    public void BadKeyCode_IsError(int code)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("toggleKey")!.Value = HotKeyValue.Create(code);

        AssertError(template, TemplateRules.BadKeyCode);
    }

    /// <summary>Легаси-форма вложенных списков — предупреждение, а не ошибка: API её понимает.</summary>
    [Fact]
    public void LegacyHotKey_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("toggleKey")!.Value = SettingValue.ParseJson("[61, [29, 157]]");

        AssertWarning(template, TemplateRules.LegacyHotKey);
        Assert.False(TemplateValidator.Validate(template).HasErrors);
    }

    /// <summary>Горячая клавиша строкой — ошибка: значение обязано быть списком кодов.</summary>
    [Fact]
    public void HotKeyAsString_IsError()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("toggleKey")!.Value = SettingValue.Of("Ctrl+F3");

        AssertError(template, TemplateRules.ValueTypeMismatch);
    }

    // ------------------------------------------------------------------ ширина, кнопка, лишнее

    /// <summary>width у типа, который его не читает, — предупреждение.</summary>
    [Fact]
    public void WidthOnCheckBox_IsWarning()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Width = 200;

        AssertWarning(template, TemplateRules.WidthNotSupported);
    }

    /// <summary>Слишком широкий контрол не влезет в колонку.</summary>
    [Fact]
    public void HugeWidth_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!.Width = 900;

        AssertWarning(template, TemplateRules.WidthTooLarge);
    }

    /// <summary>Живые значения width замечаний не вызывают.</summary>
    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(310)]
    public void LiveWidths_AreAccepted(int width)
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!.Width = width;

        Assert.False(TemplateValidator.Validate(template).Has(TemplateRules.WidthTooLarge));
    }

    /// <summary>Кнопка у типа, который её не читает, — ошибка: обработчик не вызовется никогда.</summary>
    [Fact]
    public void ButtonOnHotKey_IsError()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("toggleKey")!.Button = new ControlButton { Text = "Сброс" };

        AssertError(template, TemplateRules.ButtonNotSupported);
    }

    /// <summary>Кнопка у галочки допустима.</summary>
    [Fact]
    public void ButtonOnCheckBox_IsAccepted()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Button = new ControlButton { Text = "Сброс" };

        Assert.False(TemplateValidator.Validate(template).Has(TemplateRules.ButtonNotSupported));
    }

    /// <summary>Текст и иконка в кнопке взаимно исключают друг друга.</summary>
    [Fact]
    public void ButtonWithTextAndIcon_IsWarning()
    {
        SettingsTemplate template = Good();
        template.Column1[1].Button = new ControlButton { Text = "Сброс", IconSource = "SomeIcon" };

        AssertWarning(template, TemplateRules.ButtonTextAndIcon);
    }

    /// <summary>itemRenderer — мёртвое поле, дизайнер обязан о нём сказать.</summary>
    [Fact]
    public void ItemRenderer_IsWarning()
    {
        SettingsTemplate template = Good();
        template.FindByVarName("position")!
            .SetExtra(TemplateJson.ItemRendererKey, SettingValue.Of("DropDownListItemRendererSound"));

        AssertWarning(template, TemplateRules.DeadItemRenderer);
    }

    /// <summary>Неизвестный тип контрола — ошибка.</summary>
    [Fact]
    public void UnknownType_IsError()
    {
        SettingsTemplate template = Good();
        template.Column1.Add(new SettingControl(ControlKind.Unknown)
        {
            RawTypeName = "SuperWidget",
            Text = "Что-то",
            VarName = "superVar",
            Value = SettingValue.Of(1L)
        });

        AssertError(template, TemplateRules.UnknownType);
    }

    /// <summary>Типы, которых нет в реальных шаблонах выборки, помечаются, но ошибкой не считаются.</summary>
    [Fact]
    public void UnprovenTypes_AreInfoOnly()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        template.Column1.Add(SettingControl.TextInput("Текст", "text", "x"));
        template.Column1.Add(SettingControl.ColorChoice("Цвет", "color", "FFCC00"));
        template.Column1.Add(SettingControl.NumericStepper("Счётчик", "count", 1, 0, 5, 1));
        template.Column1.Add(SettingControl.RangeSlider("Диапазон", "range", 2, 8, 0, 10, 1));

        TemplateValidationResult result = TemplateValidator.Validate(template);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Equal(4, result.Issues.Count(i => i.Code == TemplateRules.UnprovenType));
    }

    // ------------------------------------------------------------------ шаблоны формата modsettings.dat

    /// <summary>
    /// ГЛАВНАЯ ПРОВЕРКА НА ЛОЖНЫЕ СРАБАТЫВАНИЯ: шаблоны фикстуры повторяют то, как пишут формы
    /// рабочие моды, значит ни один из них не имеет права дать ОШИБКУ. Предупреждения на них
    /// законны и перечислены отдельным тестом.
    /// </summary>
    [Fact]
    public void SampleTemplates_HaveNoErrors()
    {
        var failures = new List<string>();

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate template = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;
            TemplateValidationResult result = TemplateValidator.Validate(template);
            if (result.HasErrors) failures.Add($"{linkage}:{Environment.NewLine}{result.Describe()}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// А вот предупреждения такие шаблоны дают — и ровно те, что мы про них знаем:
    /// отсутствие settingsVersion, мёртвый itemRenderer, легаси-хоткеи, посторонний 'random',
    /// галочка со значением-числом. Тест фиксирует набор кодов, чтобы новое правило не начало
    /// шуметь незаметно.
    /// </summary>
    [Fact]
    public void SampleTemplates_WarnOnlyAboutKnownThings()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate template = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;
            foreach (TemplateIssue issue in TemplateValidator.Validate(template).Issues)
                if (issue.Severity == TemplateIssueSeverity.Warning)
                    codes.Add(issue.Code);
        }

        Assert.Equal(
            new[]
            {
                TemplateRules.NoSettingsVersion,
                TemplateRules.ExtraTemplateKey,
                TemplateRules.ValueTypeMismatch,
                TemplateRules.LegacyHotKey,
                TemplateRules.DeadItemRenderer
            }.OrderBy(c => c, StringComparer.Ordinal),
            codes);
    }

    // ------------------------------------------------------------------ помощники

    private static void AssertError(SettingsTemplate template, string code)
    {
        TemplateValidationResult result = TemplateValidator.Validate(template);
        Assert.Contains(result.Issues, i => i.Code == code && i.Severity == TemplateIssueSeverity.Error);
    }

    private static void AssertWarning(SettingsTemplate template, string code)
    {
        TemplateValidationResult result = TemplateValidator.Validate(template);
        Assert.Contains(result.Issues, i => i.Code == code && i.Severity == TemplateIssueSeverity.Warning);
    }

    /// <summary>Заведомо правильный шаблон, который тесты по одному портят.</summary>
    private static SettingsTemplate Good()
    {
        var template = new SettingsTemplate
        {
            Linkage = "ru.local.hello_tanker",
            ModDisplayName = "Hello Tanker",
            SettingsVersion = 1
        };
        template.Column1.Add(SettingControl.Label("Основное"));
        template.Column1.Add(SettingControl.CheckBox("Показывать приветствие", "showGreeting", true));
        template.Column1.Add(SettingControl.Slider("Задержка", "delaySec", 3, 0, 10, 1, "{{value}} сек."));
        template.Column2.Add(SettingControl.Dropdown("Положение", "position", 1, "Слева", "По центру", "Справа"));
        template.Column2.Add(SettingControl.HotKey("Горячая клавиша", "toggleKey", 61, HotKeyValue.Ctrl));
        return template;
    }
}
