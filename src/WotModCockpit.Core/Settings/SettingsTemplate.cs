namespace WotModCockpit.Core.Settings;

/// <summary>
/// Шаблон окна настроек мода для ModsSettingsAPI 1.6.4 — то, что уезжает первым
/// аргументом-словарём в <c>g_modsSettingsApi.setModTemplate(linkage, template, callback)</c>
/// и потом лежит в <c>modsettings.dat</c> в секции <c>templates</c>.
///
/// <para><b>Что API делает с этим объектом.</b> При регистрации он сравнивает новый шаблон
/// с сохранённым (<c>compareTemplates</c>) и, если тот «новее», ЗАМЕНЯЕТ сохранённый и
/// ПЕРЕСОБИРАЕТ настройки игрока из значений шаблона. Метода слияния старых значений в API
/// нет вообще. Отсюда два следствия, которые обязан знать дизайнер:</para>
/// <list type="number">
///   <item>без <see cref="SettingsVersion"/> сравниваются JSON шаблонов целиком, поэтому
///   правка одной буквы в подписи СБРАСЫВАЕТ настройки игрока;</item>
///   <item>с <see cref="SettingsVersion"/> шаблон принимается, только если версия ВЫРОСЛА,
///   и это тоже сбрасывает настройки — просто предсказуемо.</item>
/// </list>
/// <para>Лечится это не версией, а тем, что мод строит шаблон ОТ СОХРАНЁННЫХ у себя значений.</para>
/// </summary>
public sealed class SettingsTemplate
{
    /// <summary>
    /// Ключ мода в состоянии API. В САМ ШАБЛОН НЕ ПИШЕТСЯ: <c>generateSettingsData</c>
    /// подставляет его в копию шаблона сам, перед отдачей во Flash. В модели он лежит
    /// рядом, потому что без него шаблон некуда зарегистрировать.
    ///
    /// <para>Обязан быть неизменным ASCII: <c>generateSettingsData</c> делает
    /// <c>sorted(templates, key=str.lower)</c>, а несвязанный <c>str.lower</c> на unicode-строке
    /// в Python 2 бросает <c>TypeError</c> — и окно настроек не откроется НИ У ОДНОГО мода.</para>
    /// </summary>
    public string Linkage { get; set; } = string.Empty;

    /// <summary>Заголовок блока мода в окне. Допускает HTML-разметку (bold, size 15).</summary>
    public string ModDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Версия формы. Формально необязательна (10 из 13 живых модов её не имеют),
    /// фактически обязательна: без неё любое косметическое изменение шаблона сбрасывает
    /// настройки игрока. <c>null</c> означает «ключа в шаблоне нет».
    /// </summary>
    public int? SettingsVersion { get; set; } = 1;

    /// <summary>
    /// Есть ли у мода верхний тумблер «вкл/выкл». Наличие КЛЮЧА <c>enabled</c> в шаблоне —
    /// и есть признак: есть ключ — есть тумблер и ключ <c>enabled</c> в настройках,
    /// нет ключа — нет ни того, ни другого (так устроены шаблоны части живых модов).
    ///
    /// <para>Тумблер гасит контролы только ВИЗУАЛЬНО (alpha 0.5, mouseEnabled=false).
    /// Логику отключения мод обязан реализовать сам.</para>
    /// </summary>
    public bool HasEnabled { get; set; } = true;

    /// <summary>Значение тумблера по умолчанию. Имеет смысл только при <see cref="HasEnabled"/>.</summary>
    public bool EnabledDefault { get; set; } = true;

    /// <summary>Левая колонка. Ширина колонки в окне — около 430 px.</summary>
    public IList<SettingControl> Column1 { get; } = new List<SettingControl>();

    /// <summary>Правая колонка (x=450). Может быть пустой, но ключ <c>column2</c> пишется всегда.</summary>
    public IList<SettingControl> Column2 { get; } = new List<SettingControl>();

    /// <summary>
    /// Посторонние ключи верхнего уровня, встреченные при импорте. API их не трогает,
    /// но они участвуют в сравнении шаблонов. Живой пример: у одного из чужих модов в шаблоне лежит
    /// <c>'random': 43437</c> — при отсутствующем <c>settingsVersion</c> это заставляет API
    /// переписывать шаблон и сбрасывать настройки НА КАЖДОМ ЗАПУСКЕ. Дизайнер таких ключей
    /// не создаёт, но при импорте не выбрасывает.
    /// </summary>
    public IDictionary<string, SettingValue> Extra { get; } =
        new Dictionary<string, SettingValue>(StringComparer.Ordinal);

    /// <summary>Порядок посторонних ключей, каким он был в исходном шаблоне.</summary>
    public IList<string> ExtraOrder { get; } = new List<string>();

    /// <summary>Положить посторонний ключ верхнего уровня, сохранив порядок.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void SetExtra(string key, SettingValue value)
    {
        if (!Extra.ContainsKey(key)) ExtraOrder.Add(key);
        Extra[key] = value;
    }

    /// <summary>Все контролы обеих колонок подряд: сначала левая, потом правая.</summary>
    public IEnumerable<SettingControl> AllControls => Column1.Concat(Column2);

    /// <summary>Сколько контролов в шаблоне.</summary>
    public int ControlCount => Column1.Count + Column2.Count;

    /// <summary>Найти контрол по ключу настройки.</summary>
    /// <param name="varName">Ключ.</param>
    /// <returns>Контрол или <c>null</c>.</returns>
    public SettingControl? FindByVarName(string varName) =>
        AllControls.FirstOrDefault(c => string.Equals(c.VarName, varName, StringComparison.Ordinal));

    /// <summary>
    /// Словарь настроек, который API выведет из этого шаблона. Дословно повторяет
    /// <c>api.getSettingsFromTemplate</c>: сперва <c>enabled</c>, если ключ есть, затем по обеим
    /// колонкам пары <c>varName -&gt; value</c> у компонентов, где есть ОБА ключа
    /// (<c>Label</c> и <c>Empty</c> отсеиваются сами).
    ///
    /// <para>Это и есть <c>DEFAULTS</c> генерируемого модуля: второго источника правды
    /// в сгенерированном коде быть не должно.</para>
    /// </summary>
    /// <returns>Ключ настройки -&gt; значение по умолчанию, в порядке обхода API.</returns>
    public IReadOnlyList<KeyValuePair<string, SettingValue>> BuildDefaults()
    {
        var result = new List<KeyValuePair<string, SettingValue>>();
        if (HasEnabled)
            result.Add(new KeyValuePair<string, SettingValue>(
                TemplateJson.EnabledKey, SettingValue.Of(EnabledDefault)));

        foreach (SettingControl control in AllControls)
        {
            if (!control.ContributesSetting) continue;
            result.Add(new KeyValuePair<string, SettingValue>(control.VarName!, control.Value!));
        }
        return result;
    }

    /// <summary>Копия шаблона, ничем не связанная с исходным.</summary>
    /// <returns>Новый объект.</returns>
    public SettingsTemplate Clone()
    {
        var copy = new SettingsTemplate
        {
            Linkage = Linkage,
            ModDisplayName = ModDisplayName,
            SettingsVersion = SettingsVersion,
            HasEnabled = HasEnabled,
            EnabledDefault = EnabledDefault
        };
        foreach (SettingControl control in Column1) copy.Column1.Add(control.Clone());
        foreach (SettingControl control in Column2) copy.Column2.Add(control.Clone());
        foreach (string key in ExtraOrder) copy.SetExtra(key, Extra[key]);
        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{(Linkage.Length > 0 ? Linkage : "<без linkage>")}: {ControlCount} контролов";
}
