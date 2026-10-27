using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WotModCockpit.Core;

namespace WotModCockpit.App;

/// <summary>
/// Композиционный корень приложения. Вынесен из <see cref="App"/> отдельным статическим классом,
/// чтобы состав контейнера можно было проверить тестом, НЕ создавая <see cref="System.Windows.Application"/>
/// и не поднимая WPF-поток.
/// </summary>
public static class AppServices
{
    /// <summary>
    /// Собирает контейнер приложения: сервисы Core плюс всё, что специфично для WPF.
    /// <para>
    /// UI-агент регистрирует здесь свои ViewModel и окна. Правило: ViewModel окна — Singleton,
    /// ViewModel диалога — Transient. Сервисы Core уже добавлены
    /// через <see cref="CoreServiceCollectionExtensions.AddCockpitCore"/>.
    /// </para>
    /// </summary>
    /// <returns>Готовый к использованию провайдер. Вызывающий обязан его освободить.</returns>
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddCockpitCore();

        // Шов браузера API к индексу Core (см. ApiBrowser/ApiBrowserContracts.cs).
        //
        // CoreApiBrowserBackend требует IApiIndexService и IApiCatalogService. Если их в
        // AddCockpitCore не окажется, ValidateOnBuild уронил бы весь контейнер на старте
        // приложения — поэтому проверяем и при отсутствии кладём заглушку: экран браузера
        // тогда открывается, честно пишет своё состояние и не мешает работать кокпиту.
        //
        // Ни одна ViewModel и ни один XAML на типы Core-сервисов напрямую не ссылаются —
        // весь перевод моделей собран в CoreApiBrowserBackend.
        if (services.Any(d => d.ServiceType == typeof(Core.ApiIndex.IApiIndexService))
            && services.Any(d => d.ServiceType == typeof(Core.ApiCatalog.IApiCatalogService)))
        {
            services.AddSingleton<ApiBrowser.IApiBrowserBackend, ApiBrowser.CoreApiBrowserBackend>();
        }
        else
        {
            services.AddSingleton<ApiBrowser.IApiBrowserBackend, ApiBrowser.UnavailableApiBrowserBackend>();
        }

        // --- Этап 3: preflight, рецепты, мастер нового проекта -------------------------
        //
        // Швы описаны в Stage3/Stage3Contracts.cs, и здесь работает то же правило, что у
        // браузера API: пока адаптера к Core нет, в контейнере лежит заглушка, а экраны
        // открываются и честно объясняют своё состояние. Регистрация через TryAdd НЕ нужна:
        // адаптер, когда он появится, заменит эти три строки.
        Stage3.Stage3Registration.AddBackends(services);

        // --- Этап 4: дизайнер настроек ModsSettingsAPI ---------------------------------
        //
        // Шов описан в Stage4/SettingsDesignerContracts.cs. Регистрация идёт через TryAdd,
        // поэтому настоящая реализация побеждает встроенную просто тем, что зарегистрирована
        // ВЫШЕ: первая выигрывает.
        //
        // CoreSettingsDesignerBackend — генератор Core (SettingsCodeGenerator, TemplateValidator,
        // TemplateImporter). По сравнению со встроенным он пишет схему УЧАСТКОМ по маркерам
        // (дописанное человеком ниже участка переживает перегенерацию), добавляет 39 правил
        // MSA0xx и умеет читать форму прямо из settings_schema.py, когда settings.design.json
        // в проекте нет. Если службы Core в контейнере не окажется, остаётся встроенный
        // генератор и экран работает как прежде.
        if (services.Any(d => d.ServiceType == typeof(Core.Settings.ISettingsDesignerService)))
        {
            services.AddSingleton<Stage4.ISettingsDesignerBackend, Stage4.CoreSettingsDesignerBackend>();
        }

        Stage4.Stage4Registration.AddBackends(services);

        // --- Экран «Моды»: лаунчер установленных модов ---------------------------------
        //
        // Служба Core со всей работой по диску: обход каталога, иконки, включение/выключение,
        // установка из dist и журнал операций. Фабрика, а не AddSingleton<,>: у конструктора
        // ModLibraryService оба аргумента необязательные, а встроенный контейнер значения по
        // умолчанию не подставляет и упал бы на ValidateOnBuild (та же причина, что у
        // ApiIndexService в AddCockpitCore).
        //
        // TryAdd: если служба уже зарегистрирована в AddCockpitCore, побеждает та регистрация.
        services.TryAddSingleton<Core.Mods.IModLibraryService>(_ => new Core.Mods.ModLibraryService());

        // ViewModel экранов — Singleton, как и само окно: экраны живут всё время работы
        // приложения и обязаны сохранять состояние при переключении.
        services.AddSingleton<ViewModels.ApiBrowserViewModel>();
        services.AddSingleton<ViewModels.ModsViewModel>();
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ProblemsViewModel>();
        services.AddSingleton<ViewModels.RecipesViewModel>();
        services.AddSingleton<ViewModels.Stage3ViewModel>();
        services.AddSingleton<ViewModels.SettingsDesignerViewModel>();
        services.AddSingleton<MainWindow>();

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Ловим кривые регистрации на старте, а не в проде.
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Экран «Моды» подключается к главной модели в своём конструкторе (AttachMods), как
        // это делают оболочка этапа 3 и дизайнер настроек. Те резолвятся главным окном, а этот
        // экран окну не нужен — поэтому создаём его здесь, до показа окна, иначе чип «Моды»
        // остался бы погашенным навсегда. Никакой работы конструктор не делает: чтение каталога
        // игры начинается только при первом переключении на экран.
        provider.GetRequiredService<ViewModels.ModsViewModel>();

        return provider;
    }
}
