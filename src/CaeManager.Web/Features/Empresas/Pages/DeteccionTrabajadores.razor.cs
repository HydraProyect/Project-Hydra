using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionNuevo;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

/// <summary>
/// Detección de personal de una Empresa, contra su mockup Gen 2
/// «Deteccion Trabajadores TALVEG.dc.html». Enseña las
/// <see cref="DeteccionTrabajador"/> pendientes que propuso
/// <c>DeteccionTrabajadoresService</c> al leer por IA un documento de la
/// Empresa, y deja que una persona decida cada una: la IA propone, nunca aplica.
///
/// <para>
/// <b>Reglas de carrera:</b>
/// <list type="bullet">
/// <item>la carga (<see cref="_versionCarga"/>): la versión se captura antes
/// de cada <c>await</c>; una respuesta de una carga que ya no es la vigente —
/// otra empresa, un reintento, una recarga posterior— se descarta;</item>
/// <item>las resoluciones (<see cref="_procesandoId"/>): una sola a la vez en
/// toda la página. La marca se levanta antes del primer <c>await</c>, así que
/// el segundo evento de un doble clic —o un clic en otra fila mientras la
/// primera viaja— la encuentra puesta y no envía nada;</item>
/// <item>la retirada (<see cref="Dispose"/>): cancela las consultas en vuelo y
/// una respuesta que llegue después no pinta ni recarga.</item>
/// </list>
/// </para>
/// </summary>
public partial class DeteccionTrabajadores : ComponentBase, IDisposable
{
    [Parameter] public Guid EmpresaId { get; set; }

    private EmpresaDetalleDto? _empresa;
    private IReadOnlyList<DeteccionTrabajadorDto> _detecciones = [];
    private bool _cargando = true;
    private bool _errorCarga;

    /// <summary>Hay una lista pintada: una recarga la deja a la vista, marcada como ocupada, en vez de volver al esqueleto.</summary>
    private bool _listaPintada;

    private int _versionCarga;
    private Guid? _empresaCargada;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>La detección que se está resolviendo. Mientras no sea null, ninguna otra acción de fila envía nada.</summary>
    private Guid? _procesandoId;

    /// <summary>Fila marcada por los atajos j/k.</summary>
    private Guid? _idEnfocado;

    // Baja pendiente de confirmar: elimina (soft delete) al trabajador y le
    // cierra las asignaciones activas, así que no sale de un solo clic.
    private DeteccionTrabajadorDto? _bajaPendiente;
    private bool _confirmarBajaVisible;
    private bool _dandoDeBaja;

    // Descarte pendiente de confirmar: cierra la detección sin posibilidad de
    // reabrirla desde ninguna pantalla.
    private DeteccionTrabajadorDto? _descartePendiente;
    private bool _confirmarDescarteVisible;
    private bool _descartando;

    // Mismo orden en que se pintan: el de ObtenerDeteccionesPorEmpresaQuery.
    private List<DeteccionTrabajadorDto> Nuevos => _detecciones.Where(d => d.Tipo == TipoDeteccion.Nuevo).ToList();
    private List<DeteccionTrabajadorDto> Ausentes => _detecciones.Where(d => d.Tipo == TipoDeteccion.Ausente).ToList();

    private DateTime DeteccionMasReciente => _detecciones.Max(d => d.CreadaEnUtc);
    private DateTime DeteccionMasAntigua => _detecciones.Min(d => d.CreadaEnUtc);

    private string TextoPendientes
    {
        get
        {
            var total = _detecciones.Count;
            var nuevos = Nuevos.Count;
            var ausentes = Ausentes.Count;
            return $"{total} ({nuevos} {(nuevos == 1 ? "nuevo" : "nuevos")} · {ausentes} {(ausentes == 1 ? "ausente" : "ausentes")})";
        }
    }

    /// <summary>
    /// La carga va aquí y no en <c>OnInitializedAsync</c>: navegar de la
    /// detección de una empresa a la de otra reutiliza el componente y solo
    /// cambia el parámetro. Lo de la empresa anterior —lista, diálogos, foco—
    /// se retira antes de pedir lo de la nueva.
    /// </summary>
    protected override Task OnParametersSetAsync()
    {
        if (_empresaCargada == EmpresaId)
            return Task.CompletedTask;

        _empresaCargada = EmpresaId;
        _empresa = null;
        _detecciones = [];
        _listaPintada = false;
        _idEnfocado = null;
        _bajaPendiente = null;
        _confirmarBajaVisible = false;
        _descartePendiente = null;
        _confirmarDescarteVisible = false;
        return CargarAsync();
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private Task ReintentarAsync() => CargarAsync();

    private bool EsVigente(int version) => !_desechado && version == _versionCarga;

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var version = ++_versionCarga;
        var empresaId = EmpresaId;
        var token = _ciclo.Token;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var empresa = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId), token);
            if (!EsVigente(version))
                return;

            if (empresa is null)
            {
                _errorCarga = true;
                _listaPintada = false;
                return;
            }

            var resultado = await Mediator.Send(new ObtenerDeteccionesPorEmpresaQuery(empresaId), token);
            if (!EsVigente(version))
                return;

            if (resultado.EsFallido)
            {
                _errorCarga = true;
                _listaPintada = false;
                return;
            }

            _empresa = empresa;
            _detecciones = resultado.Valor;
            _listaPintada = true;
            if (_idEnfocado is { } enfocado && _detecciones.All(d => d.Id != enfocado))
                _idEnfocado = null;
        }
        catch (Exception)
        {
            if (EsVigente(version))
            {
                _errorCarga = true;
                _listaPintada = false;
            }
        }
        finally
        {
            if (EsVigente(version))
                _cargando = false;
        }
    }

    // ---------------------------------------------------------------- nuevos

    private Task DarDeAltaAsync(DeteccionTrabajadorDto deteccion) => ResolverNuevoAsync(deteccion, crear: true);

    private void PedirDescarte(DeteccionTrabajadorDto deteccion)
    {
        if (_procesandoId is not null)
            return;

        _descartePendiente = deteccion;
        _confirmarDescarteVisible = true;
    }

    private async Task ConfirmarDescarteAsync()
    {
        if (_descartePendiente is not { } deteccion)
            return;

        _descartando = true;
        try
        {
            if (await ResolverNuevoAsync(deteccion, crear: false))
            {
                _confirmarDescarteVisible = false;
                _descartePendiente = null;
            }
        }
        finally
        {
            _descartando = false;
        }
    }

    /// <returns><c>true</c> si la detección quedó resuelta; <c>false</c> si no se envió o falló, ya avisado.</returns>
    private async Task<bool> ResolverNuevoAsync(DeteccionTrabajadorDto deteccion, bool crear)
    {
        // Sin await entre esta comprobación y la marca: el segundo evento de
        // un doble clic ya la encuentra puesta.
        if (_procesandoId is not null)
            return false;

        _procesandoId = deteccion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionNuevoCommand(deteccion.Id, crear));
            if (resultado.EsFallido)
            {
                // «Dar de alta» puede fallar por DNI ya existente o por datos
                // no válidos: la detección sigue pendiente y el mensaje del
                // handler dice qué hacer.
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return false;
            }

            ToastService.Mostrar(
                crear ? $"{NombreDe(deteccion)} dado de alta como trabajador." : "Detección descartada.",
                TonoToast.Exito);
            await TrasResolverAsync(deteccion.Id);
            return true;
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos aplicar el cambio. Intenta nuevamente.", TonoToast.Error);
            return false;
        }
        finally
        {
            _procesandoId = null;
        }
    }

    // ---------------------------------------------------------------- ausentes

    /// <summary>
    /// «Mantener activo» no destruye nada —solo da la detección por resuelta— y
    /// por eso sigue siendo un clic. La baja pasa siempre por
    /// <see cref="AbrirConfirmarBaja"/> y <see cref="ConfirmarBajaAsync"/>: ningún
    /// otro camino envía el comando con desactivar a true.
    /// </summary>
    private Task MantenerActivoAsync(DeteccionTrabajadorDto deteccion) => EnviarResolucionAusenteAsync(deteccion, desactivar: false);

    private void AbrirConfirmarBaja(DeteccionTrabajadorDto deteccion)
    {
        if (_procesandoId is not null)
            return;

        _bajaPendiente = deteccion;
        _confirmarBajaVisible = true;
    }

    private async Task ConfirmarBajaAsync()
    {
        if (_bajaPendiente is not { } deteccion)
            return;

        _dandoDeBaja = true;
        try
        {
            if (await EnviarResolucionAusenteAsync(deteccion, desactivar: true))
            {
                _confirmarBajaVisible = false;
                _bajaPendiente = null;
            }
        }
        finally
        {
            _dandoDeBaja = false;
        }
    }

    /// <returns>
    /// <c>true</c> si la operación terminó —se aplicase la baja o no hiciera
    /// falta—; <c>false</c> si no se envió o falló, ya avisado con un toast.
    /// Una excepción también acaba en <c>false</c> con aviso, y el diálogo de
    /// baja queda abierto para reintentar o cancelar.
    /// </returns>
    private async Task<bool> EnviarResolucionAusenteAsync(DeteccionTrabajadorDto deteccion, bool desactivar)
    {
        if (_procesandoId is not null)
            return false;

        _procesandoId = deteccion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionAusenteCommand(deteccion.Id, desactivar));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return false;
            }

            // El mensaje sale de lo que el comando dice que hizo, no de lo que se
            // le pidió: pedir una baja y que el trabajador ya no estuviera activo
            // no es una baja.
            var (mensaje, tono) = resultado.Valor switch
            {
                ResultadoResolucionAusente.DadoDeBaja => ("Trabajador dado de baja.", TonoToast.Exito),
                ResultadoResolucionAusente.YaNoEstabaActivo => (
                    "Este trabajador ya no estaba activo, así que no había nada que dar de baja. La detección queda cerrada.",
                    TonoToast.Info),
                _ => ("Trabajador mantenido activo.", TonoToast.Exito),
            };
            ToastService.Mostrar(mensaje, tono);
            await TrasResolverAsync(deteccion.Id);
            return true;
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos aplicar el cambio. Intenta nuevamente.", TonoToast.Error);
            return false;
        }
        finally
        {
            _procesandoId = null;
        }
    }

    /// <summary>
    /// La fila resuelta sale de la lista en el acto y la lista se vuelve a
    /// pedir por detrás, sin esqueleto. Si la página ya se retiró, no se pide nada.
    /// </summary>
    private Task TrasResolverAsync(Guid deteccionId)
    {
        if (_desechado)
            return Task.CompletedTask;

        _detecciones = _detecciones.Where(d => d.Id != deteccionId).ToList();
        return CargarAsync();
    }

    // ---------------------------------------------------------------- atajos

    private Task ManejarAtajoAsync(string tecla)
    {
        var secuencia = Nuevos.Concat(Ausentes).Select(d => d.Id).ToList();
        if (secuencia.Count == 0)
            return Task.CompletedTask;

        var actual = _idEnfocado is { } id ? secuencia.IndexOf(id) : -1;
        switch (tecla)
        {
            case "j":
                _idEnfocado = secuencia[Math.Min(actual + 1, secuencia.Count - 1)];
                break;
            case "k":
                _idEnfocado = secuencia[Math.Max(actual - 1, 0)];
                break;
            default:
                return Task.CompletedTask;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- textos

    private static string NombreDe(DeteccionTrabajadorDto? deteccion) =>
        deteccion is null ? string.Empty : $"{deteccion.Nombre} {deteccion.Apellidos}".Trim();

    private static string FormatearFecha(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    private enum Comprobacion { DigitoCorrecto, DigitoIncorrecto, NoEsDniNiNie }

    /// <summary>
    /// El mismo criterio con el que <c>DeteccionTrabajadoresService</c> decide
    /// si propone un alta: DNI o NIE con dígito de control (módulo 23), tras
    /// quitar guiones y espacios. Se calcula con
    /// <see cref="ValidadorIdentificacion"/> sobre el DNI que trae la detección;
    /// el servicio usa su propia copia privada del algoritmo, así que esta
    /// columna no puede afirmar nada más que lo que se ve: si el identificador
    /// cuadra, no por qué se propuso.
    /// </summary>
    private static Comprobacion Comprobar(string dni)
    {
        var limpio = dni.Replace("-", string.Empty).Replace(" ", string.Empty);
        var resultado = ValidadorIdentificacion.Analizar(limpio);
        return resultado.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie
            ? resultado.EsValido ? Comprobacion.DigitoCorrecto : Comprobacion.DigitoIncorrecto
            : Comprobacion.NoEsDniNiNie;
    }

    private static string TextoComprobacion(Comprobacion comprobacion) => comprobacion switch
    {
        Comprobacion.DigitoCorrecto => "Dígito de control correcto",
        Comprobacion.DigitoIncorrecto => "Dígito de control incorrecto",
        _ => "No es DNI ni NIE: sin comprobar"
    };

    private static TonoBadge TonoComprobacion(Comprobacion comprobacion) => comprobacion switch
    {
        Comprobacion.DigitoCorrecto => TonoBadge.Exito,
        Comprobacion.DigitoIncorrecto => TonoBadge.Peligro,
        _ => TonoBadge.Advertencia
    };
}
