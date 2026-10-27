using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Core;

/// <summary>
/// Регистрация сервисов Core в DI. Живёт в Core (а не в App), чтобы состав контейнера
/// можно было проверять тестами без WPF.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет все сервисы Core.
    /// <para>Время жизни выбрано так:</para>
    /// <list type="bullet">
    ///   <item><see cref="IGameInstallLocator"/>, <see cref="IProjectStore"/>, <see cref="ILogParser"/>,
    ///         <see cref="ITracebackMapper"/>, <see cref="IDeployProbe"/>, <see cref="IBuildRunner"/>,
    ///         <see cref="IEditorLauncher"/>, <see cref="ISettingsStore"/> — <b>Singleton</b>:
    ///         это чистые сервисы без состояния запроса, часть из них кэширует результаты
    ///         (поиск VS Code, скомпилированные регэкспы).</item>
    ///   <item><see cref="ILogTailer"/> напрямую НЕ регистрируется — он владеет дескриптором файла
    ///         и создаётся под конкретный путь. Вместо него в контейнере лежит
    ///         <see cref="ILogTailerFactory"/> (Singleton), а созданным тейлером владеет вызывающий.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <returns>Та же коллекция для цепочки вызовов.</returns>
    public static IServiceCollection AddCockpitCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IGameInstallLocator, PathsXmlGameInstallLocator>();
        services.AddSingleton<IProjectStore, JsonProjectStore>();
        services.AddSingleton<IBuildRunner, PythonBuildRunner>();
        services.AddSingleton<ILogParser, PythonLogParser>();
        services.AddSingleton<ILogTailerFactory, PollingLogTailerFactory>();
        services.AddSingleton<ITracebackMapper, WotSrcTracebackMapper>();
        services.AddSingleton<IDeployProbe, FileSystemDeployProbe>();
        services.AddSingleton<IEditorLauncher, VsCodeEditorLauncher>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();

        // --- Этап 2, браузер API ---------------------------------------------------------
        // SourceFileService — Singleton, потому что он и есть кеш прочитанных файлов;
        // IApiIndexService — Singleton, потому что индекс (27 МБ на 8052 файла, 166 436 символов) строится
        // один раз в фоне и живёт на всё время работы приложения. Оба обязаны корректно
        // существовать без дерева исходников: тогда индекс пустой, а статус говорит,
        // что путь надо указать.
        // Фабрика, а не AddSingleton<IApiIndexService, ApiIndexService>(): у конструктора есть
        // необязательный ApiIndexOptions, а встроенный контейнер значения по умолчанию
        // не подставляет и упал бы на ValidateOnBuild.
        services.AddSingleton<SourceFileService>();
        services.AddSingleton<IApiIndexService>(sp => new ApiIndexService(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<SourceFileService>()));

        // Доменные каталоги: события, сервисы, горячие точки. Singleton по той же причине,
        // что и индекс: снимок строится один раз на дерево исходников, в фоне через BuildAsync,
        // и обязан существовать без дерева (тогда каталоги пустые, а статус — NoSources).
        services.AddSingleton<ApiCatalog.IApiCatalogService, ApiCatalog.ApiCatalogService>();

        // --- Этап 3: каталог рецептов, мастер проекта, pyright --------------------------
        // RecipeService — Singleton: каталог неизменен, а карта «модуль → файл» по индексу
        // строится один раз на снимок и переиспользуется всеми проверками совместимости.
        services.AddSingleton<Recipes.IRecipeService>(sp => new Recipes.RecipeService(
            sp.GetRequiredService<ApiIndex.IApiIndexService>()));

        // Мастер проекта не имеет состояния; путь к шаблону приходит в ScaffoldOptions
        // (мастер подставляет туда выбранный или запомненный шаблон), а при пустом значении
        // ищется через TemplateLocator: WOTCOCKPIT_TEMPLATE_DIR, затем соседние каталоги.
        services.AddSingleton<Scaffold.IProjectScaffolder>(_ => new Scaffold.ProjectScaffolder());

        // PyrightRunner кэширует результат поиска pyright, поэтому Singleton.
        // Отсутствие Node.js — штатное состояние, конструктор от этого не падает.
        //
        // Пути берутся ИЗ НАСТРОЕК, а не остаются мёртвыми параметрами конструктора:
        // автопоиск покрывает node_modules рядом с кокпитом, PATH и %APPDATA%\npm, а
        // установку с нестандартным префиксом без настройки было бы нечем указать.
        services.AddSingleton<Lint.IPyrightRunner>(sp =>
        {
            Models.CockpitSettings settings = LoadSettingsSafely(sp);
            return new Lint.PyrightRunner(settings.PyrightPath, settings.NodePath, settings.TemplateDir);
        });

        // Preflight-анализатор. Singleton: служба держит кеш оракула клиентского API
        // (множества модулей и экспортов, построенные по индексу), который дорого строить
        // и который зависит только от индекса. Набор правил неизменяемый и потокобезопасный.
        services.AddSingleton<Preflight.PreflightAnalyzer>();
        services.AddSingleton<Preflight.IPreflightService>(sp => new Preflight.PreflightService(
            sp.GetRequiredService<IApiIndexService>(),
            sp.GetRequiredService<SourceFileService>(),
            sp.GetRequiredService<Preflight.PreflightAnalyzer>()));

        // --- Этап 4: дизайнер настроек ModsSettingsAPI ----------------------------------
        // Singleton и без состояния: служба работает поверх файлов конкретного проекта,
        // который приходит параметром, а каркас генерируемого модуля читается из встроенного
        // ресурса один раз (Lazy внутри SettingsCodeGenerator).
        services.AddSingleton<Settings.ISettingsDesignerService, Settings.SettingsDesignerService>();

        return services;
    }

    /// <summary>
    /// Читает настройки для фабрики сервиса. Битый или недоступный settings.json не должен
    /// мешать контейнеру собраться: <see cref="Abstractions.ISettingsStore.Load"/> и так
    /// не бросает, но фабрика в DI обязана быть устойчивой и к неожиданностям реализации.
    /// </summary>
    private static Models.CockpitSettings LoadSettingsSafely(IServiceProvider provider)
    {
        try
        {
            return provider.GetRequiredService<Abstractions.ISettingsStore>().Load();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new Models.CockpitSettings();
        }
    }
}
