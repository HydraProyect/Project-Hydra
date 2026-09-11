using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Auditoria.Pages;

/// <summary>
/// Rastro de <c>RegistroAccesoDocumentoSensible</c>, gateado en
/// <c>AccesosDocumentosSensibles.razor</c> por
/// <c>Policies.ConsultarAccesoDocumentosSensibles</c> (RequireRole
/// Administrador + RequireClaim del permiso específico de DEC-36).
///
/// <para>
/// Solo lectura. La consulta sigue siendo la mínima de HO-099-01 § 8: sin
/// filtros ni exportación, así que la pantalla tampoco los ofrece aunque el
/// mockup Gen 2 los proponga como alcance nuevo — filtrar en cliente una
/// página de 30 filas diría «no hubo accesos» cuando solo no están en esta
/// página.
/// </para>
/// </summary>
public partial class AccesosDocumentosSensibles : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;

    /// <summary>
    /// Identity no encuentra el Id. AspNetUsers no tiene RLS ni filtro de
    /// tenant, así que no es un usuario de otro tenant oculto: no existe.
    /// </summary>
    private const string UsuarioNoEncontrado = "(usuario eliminado)";

    /// <summary>Tamaños del selector del paginador, los del mockup: 30 es el de partida.</summary>
    private static readonly IReadOnlyList<int> TamanosPagina = [30, 50, 100];

    private IReadOnlyList<AccesoDocumentoSensibleDto> _accesos = [];
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;
    private int _totalPaginas = 1;
    private int _totalElementos;
    private int _tamanoPagina = TamanosPagina[0];

    /// <summary>
    /// Número de la última carga. Cada carga captura el suyo ANTES del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: una respuesta lenta de la página anterior no puede pintar sus
    /// filas bajo el paginador de la página nueva. En un rastro de auditoría,
    /// enseñar filas de otra pregunta es justo lo que no puede pasar.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Se cancela al salir de la página: la consulta en curso deja de trabajar
    /// para nadie y ninguna respuesta tardía repinta un componente retirado.
    /// Mismo patrón que DeteccionTrabajadores y Empresas.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    protected override Task OnInitializedAsync() => CargarAsync();

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return CargarAsync();
    }

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var consulta = new ObtenerAccesosDocumentosSensiblesQuery(_pagina, _tamanoPagina);
        var token = _ciclo.Token;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(consulta, token);
            if (!EsVigente(carga))
                return;

            var idsFaltantes = resultado.Elementos
                .Where(a => a.UsuarioId is { } id && !_nombresPorUsuarioId.ContainsKey(id))
                .Select(a => a.UsuarioId!.Value)
                .Distinct()
                .ToList();

            // Por la puerta: UserManager no pasa por MediatR y esta carga
            // corre en paralelo con los componentes del layout sobre el mismo
            // DbContext scoped (ver PuertaAccesoDatos). Mismo criterio que
            // Auditoria.razor.cs.
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    token.ThrowIfCancellationRequested();
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _nombresPorUsuarioId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? UsuarioNoEncontrado;
                }
            }, token);

            // La caché de nombres sí puede quedarse lo resuelto por una carga
            // superada (un nombre por Id no depende de la página); las filas no.
            if (!EsVigente(carga))
                return;

            _accesos = resultado.Elementos;
            _totalPaginas = resultado.TotalPaginas;
            _totalElementos = resultado.TotalElementos;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// «Quién». Hoy es el actor real: el registro guarda
    /// <c>UsuarioSimulado ?? ActorReal</c> y la única construcción del actor
    /// en producción (<c>ActorAuditoriaDesdeSesion</c>) deja el simulado en
    /// null. Cuando exista la impersonación, este Id pasará a nombrar al
    /// usuario simulado y la pantalla necesitará que
    /// <see cref="AccesoDocumentoSensibleDto"/> proyecte también
    /// <c>ActorRealUsuarioId</c> para separarlos — el contrato de lenguaje
    /// prohíbe atribuir la acción solo al simulado. Eso es un cambio de la
    /// consulta, no de esta pantalla.
    /// </summary>
    private string NombreDe(Guid? usuarioId) =>
        usuarioId is not { } id
            ? "Sistema"
            : _nombresPorUsuarioId.GetValueOrDefault(id, UsuarioNoEncontrado);

    /// <summary>Un autor que Identity ya no encuentra se atenúa para no leerse como una persona real.</summary>
    private string ClaseQuien(Guid? usuarioId) =>
        usuarioId is { } id && _nombresPorUsuarioId.GetValueOrDefault(id) == UsuarioNoEncontrado
            ? "accesos-sensibles-quien accesos-sensibles-quien-no-resuelto"
            : "accesos-sensibles-quien";

    /// <summary>Formato de fecha del resto de la app: sin segundos.</summary>
    private static string Cuando(DateTime ocurridoEnUtc) => ocurridoEnUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    private static string TextoCategoria(SensibilidadDocumental sensibilidad) => sensibilidad switch
    {
        SensibilidadDocumental.CategoriaEspecialSalud => "Salud (categoría especial)",
        SensibilidadDocumental.DatosPersonales => "Datos personales",
        _ => "Sin datos personales"
    };

    /// <summary>Tonos del mockup: salud en rojo, datos personales en ámbar, el resto neutro.</summary>
    private static TonoBadge TonoCategoria(SensibilidadDocumental sensibilidad) => sensibilidad switch
    {
        SensibilidadDocumental.CategoriaEspecialSalud => TonoBadge.Peligro,
        SensibilidadDocumental.DatosPersonales => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    private static string TextoTipoAcceso(TipoAccesoDocumentoSensible tipo) =>
        tipo == TipoAccesoDocumentoSensible.VersionAnterior ? "Versión anterior" : "Apertura";

    /// <summary>«Sesión privilegiada» gana: es el único contexto en ámbar.</summary>
    private static string TextoContexto(AccesoDocumentoSensibleDto acceso) =>
        acceso.EsPrivilegiado ? "Sesión privilegiada"
        : acceso.ViaAcceso == TipoViaAccesoAuditoria.OperacionDelegada ? "Operación delegada"
        : "Normal";

    private static TonoBadge TonoContexto(AccesoDocumentoSensibleDto acceso) =>
        acceso.EsPrivilegiado ? TonoBadge.Advertencia : TonoBadge.Neutro;
}
