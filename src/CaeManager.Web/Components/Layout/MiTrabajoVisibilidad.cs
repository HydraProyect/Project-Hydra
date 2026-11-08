using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Opción de configuración (<c>MiTrabajo:Oculto</c>) que oculta Mi trabajo (<c>/mi-trabajo</c> y su
/// forma de un solo Tenant, <c>/bandeja</c>) para la demo con IT del 2026-11-10: Chris decidió
/// enfocar la demo en el flujo de Gestiones (DECISION_LOG, 2026-11-08, «Primera tanda de decisiones
/// pendientes», punto 4). Es ocultación, no borrado: las páginas, sus Queries y sus tests siguen
/// donde estaban. Para revertirla basta <c>MiTrabajo:Oculto=false</c>, sin tocar código.
/// </summary>
public sealed class MiTrabajoOptions
{
    public const string SeccionConfiguracion = "MiTrabajo";

    /// <summary>
    /// Falso por defecto a propósito: un host que no la configura (los tests de bUnit resuelven siempre un
    /// <c>IOptions</c> vacío) ve Mi trabajo como siempre. <c>Program.cs</c> de la Web la enciende (true) antes de
    /// leer la configuración, y <c>MiTrabajo:Oculto=false</c> la apaga.
    /// </summary>
    public bool Oculto { get; set; }
}

/// <summary>
/// Una sola puerta para saber si Mi trabajo está oculto: el menú lateral, la paleta Ctrl/Cmd+K y los
/// atajos (por <see cref="ContextoMenuLateral.MiTrabajoOculto"/>), los enlaces de otras pantallas y la
/// redirección de las dos rutas lo consultan aquí y no deciden por su cuenta.
/// </summary>
public static class MiTrabajoVisibilidad
{
    /// <summary>
    /// Adónde llevan las rutas de Mi trabajo mientras está oculto: la lista de Gestiones, el flujo que la
    /// demo quiere enseñar. La portada tras iniciar sesión sigue siendo Inicio (desde 2026-10-21 lo es para
    /// todos los roles); Mi trabajo ya no era portada de ningún rol.
    /// </summary>
    public const string RutaAlternativa = "/gestiones";

    /// <summary>
    /// Registro de la opción para el host real: la enciende y luego deja que <c>MiTrabajo:Oculto</c> de la
    /// configuración (appsettings, variable <c>MiTrabajo__Oculto</c>) la sobrescriba, de modo que <c>false</c> la revierte.
    /// Lo llama <c>Program.cs</c>; un test lo compone con y sin ese valor y otro vigila que <c>Program.cs</c> lo llame.
    /// </summary>
    public static IServiceCollection RegistrarOpcion(this IServiceCollection servicios, IConfiguration configuracion)
    {
        servicios.Configure<MiTrabajoOptions>(opciones => opciones.Oculto = true);
        servicios.Configure<MiTrabajoOptions>(configuracion.GetSection(MiTrabajoOptions.SeccionConfiguracion));
        return servicios;
    }

    /// <summary>
    /// Sin la opción configurada (un host de pruebas, otro host) Mi trabajo se comporta como siempre: solo
    /// <c>Program.cs</c> de la Web la enciende, y queda oculto salvo que la configuración diga lo contrario.
    /// </summary>
    public static bool EstaOculto(IServiceProvider? servicios) =>
        servicios?.GetService<IOptions<MiTrabajoOptions>>()?.Value.Oculto ?? false;
}
