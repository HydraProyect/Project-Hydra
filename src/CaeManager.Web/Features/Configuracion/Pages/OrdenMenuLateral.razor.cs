using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Configuracion.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Configuracion.Pages;

/// <summary>
/// Orden global del menú lateral (decisión del propietario del 2026-09-23; mockup
/// «Orden del Menu TALVEG.dc.html»). El Actor de Plataforma TALVEG ordena los grupos y los
/// enlaces dentro de cada grupo; el orden es el mismo para todos los usuarios de todos los
/// Tenants y solo recoloca: la visibilidad sigue siendo la de cada enlace
/// (<see cref="CatalogoMenuLateral.Visibles"/>).
///
/// <para>
/// La página lleva <c>[Authorize]</c> sin rol, como <c>Plataforma.razor</c>: su autoridad es la
/// capacidad AdminPlataforma global, no el rol Administrador que gatea el hub de Configuración
/// (ADR-011 § 1, platform privilege ≠ rol de negocio). El gate de aquí es de interfaz y usa el
/// mismo predicado que el comando (<see cref="EsAdministradorPlataformaQuery"/>); la barrera real
/// es <see cref="GuardarOrdenMenuLateralCommand"/> en Application y la RLS de la tabla.
/// </para>
/// </summary>
public partial class OrdenMenuLateral : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosConfiguracion> Textos { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private ILogger<OrdenMenuLateral> Logger { get; set; } = default!;

    private const string CodigoSinPermiso = "OrdenMenu.SinPermiso";

    private static readonly TimeZoneInfo? ZonaMadrid =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Madrid", out var zona) ? zona : null;

    private enum TipoFila { Grupo, Enlace }

    private enum Aviso { Ninguno, Guardado, Error, Conflicto }

    /// <summary>Una fila reordenable. Un enlace solo se mueve dentro de su grupo.</summary>
    private sealed record Fila(TipoFila Tipo, string GrupoId, string Id)
    {
        public string Clave => $"{Tipo}:{GrupoId}:{Id}";
    }

    /// <summary>Lo que pinta una fila: el marcado no calcula nada (ver TextosSinLocalizarCongeladosTests).</summary>
    private sealed record FilaEditable(
        Fila Fila, int Indice, int Total, string Nombre, string? Icono, string? Ruta,
        bool NacePlegado, bool EsNueva, int? Recuento);

    private sealed record GrupoEditable(FilaEditable Cabecera, IReadOnlyList<FilaEditable> Enlaces);

    private sealed record EnlacePrevio(string Id, string Icono, string Rotulo);

    private sealed record GrupoPrevio(string Id, string Titulo, IReadOnlyList<EnlacePrevio> Enlaces);

    private sealed record Previa(IReadOnlyList<GrupoPrevio> Grupos, string Texto);

    private bool? _esAdministradorPlataforma;
    private bool _cargando = true;
    private bool _guardando;

    private OrdenMenuLateralDto? _orden;
    private string? _autorOrden;
    private OrdenMenuLateralDto? _ordenConflicto;
    private string? _autorConflicto;

    private List<string> _grupos = [];
    private Dictionary<string, List<string>> _enlaces = [];
    private List<string> _gruposGuardados = [];
    private Dictionary<string, List<string>> _enlacesGuardados = [];
    private HashSet<string> _gruposNuevos = [];
    private HashSet<string> _enlacesNuevos = [];

    private Fila? _cogida;
    private List<string>? _copiaAntesDeCoger;
    private Fila? _arrastre;
    private string? _destinoArrastre;

    private string _rol = Roles.Administrador;
    private bool _confirmarRestablecer;
    private Aviso _aviso;
    private Func<Task>? _ultimaAccion;
    private string _anuncio = string.Empty;

    private readonly Dictionary<string, ElementReference> _asas = [];
    private string? _asaPorEnfocar;

    protected override async Task OnInitializedAsync()
    {
        _esAdministradorPlataforma = await Mediator.Send(new EsAdministradorPlataformaQuery());
        if (_esAdministradorPlataforma != true)
        {
            _cargando = false;
            return;
        }

        _orden = await Mediator.Send(new ObtenerOrdenMenuLateralQuery());
        _autorOrden = await AutorDeAsync(_orden);
        AplicarGuardado();
        _cargando = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Con @key la fila movida conserva su elemento, pero el navegador le quita el foco al
        // recolocarlo: se devuelve al asa para que las flechas sigan moviendo la misma fila.
        if (_asaPorEnfocar is { } clave && _asas.TryGetValue(clave, out var asa))
        {
            _asaPorEnfocar = null;
            try
            {
                await asa.FocusAsync();
            }
            catch (InvalidOperationException)
            {
                // El asa ya no está en el DOM (la lista se recargó): no hay foco que devolver.
            }
        }
    }

    // ------------------------------------------------------------------ estado guardado

    /// <summary>
    /// Reconcilia el orden guardado con el catálogo (lo nuevo al final de su lista, lo que ya no
    /// existe se ignora) y lo deja como punto de partida de la edición.
    /// </summary>
    private void AplicarGuardado()
    {
        var grupos = CatalogoMenuLateral.Reconciliar(CatalogoMenuLateral.Grupos, g => g.Id, _orden?.Grupos);
        var enlaces = CatalogoMenuLateral.Reconciliar(CatalogoMenuLateral.Enlaces, e => e.Id, _orden?.Enlaces);

        _gruposGuardados = grupos.Select(g => g.Id).ToList();
        _enlacesGuardados = grupos.ToDictionary(
            g => g.Id,
            g => enlaces.Where(e => e.GrupoId == g.Id).Select(e => e.Id).ToList());

        // «Nuevo» solo tiene sentido frente a un orden guardado: sin él, todo es el orden por defecto.
        _gruposNuevos = NoIncluidos(CatalogoMenuLateral.Grupos.Select(g => g.Id), _orden?.Grupos);
        _enlacesNuevos = NoIncluidos(CatalogoMenuLateral.Enlaces.Select(e => e.Id), _orden?.Enlaces);

        Descartar();
    }

    private static HashSet<string> NoIncluidos(IEnumerable<string> catalogo, IReadOnlyList<string>? guardado) =>
        guardado is null || guardado.Count == 0
            ? []
            : catalogo.Except(guardado, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    private void Descartar()
    {
        _grupos = [.. _gruposGuardados];
        _enlaces = _enlacesGuardados.ToDictionary(par => par.Key, par => par.Value.ToList());
        _cogida = null;
        _copiaAntesDeCoger = null;
    }

    private async Task<string?> AutorDeAsync(OrdenMenuLateralDto? orden)
    {
        if (orden is null)
            return null;

        // Por la puerta: UserManager no pasa por MediatR y comparte el DbContext scoped con los
        // componentes del layout (ver PuertaAccesoDatos).
        ApplicationUser? usuario = null;
        await PuertaAccesoDatos.EjecutarAsync(async () =>
            usuario = await UserManager.FindByIdAsync(orden.ActualizadoPorUsuarioId.ToString()));
        // El mockup identifica al Actor real por su correo; un usuario ya borrado no rompe la página.
        return new[] { usuario?.Email, usuario?.NombreCompleto }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
               ?? Textos["OrdenMenuActorDesconocido"].Value;
    }

    // ------------------------------------------------------------------ edición

    private IReadOnlyList<GrupoEditable> GruposEditables => _grupos
        .Select((id, i) =>
        {
            var grupo = CatalogoMenuLateral.Grupos.First(g => g.Id == id);
            var enlaces = _enlaces[id];
            return new GrupoEditable(
                new FilaEditable(new Fila(TipoFila.Grupo, string.Empty, id), i, _grupos.Count, grupo.Titulo, null, null,
                    !grupo.AbiertoPorDefecto, _gruposNuevos.Contains(id), enlaces.Count),
                enlaces.Select((idEnlace, j) =>
                {
                    var enlace = CatalogoMenuLateral.Enlaces.First(e => e.Id == idEnlace);
                    return new FilaEditable(new Fila(TipoFila.Enlace, id, idEnlace), j, enlaces.Count, enlace.Rotulo,
                        enlace.Icono, "/" + enlace.Ruta, false, _enlacesNuevos.Contains(idEnlace), null);
                }).ToList());
        })
        .ToList();

    private List<string> Lista(Fila fila) =>
        fila.Tipo == TipoFila.Grupo ? _grupos : _enlaces[fila.GrupoId];

    private string Nombre(Fila fila) =>
        fila.Tipo == TipoFila.Grupo
            ? CatalogoMenuLateral.Grupos.First(g => g.Id == fila.Id).Titulo
            : CatalogoMenuLateral.Enlaces.First(e => e.Id == fila.Id).Rotulo;

    private bool EsCogida(Fila fila) => _cogida == fila;

    private void Mover(Fila fila, int desplazamiento)
    {
        var lista = Lista(fila);
        var origen = lista.IndexOf(fila.Id);
        var destino = origen + desplazamiento;
        if (origen < 0 || destino < 0 || destino >= lista.Count)
            return;

        (lista[origen], lista[destino]) = (lista[destino], lista[origen]);
        TrasMover(fila, destino, lista.Count);
    }

    private void MoverA(Fila fila, Fila destinoFila)
    {
        // Solo dentro de la misma lista: un enlace no puede cambiar de grupo.
        if (fila.Tipo != destinoFila.Tipo || fila.GrupoId != destinoFila.GrupoId || fila.Id == destinoFila.Id)
            return;

        var lista = Lista(fila);
        var destino = lista.IndexOf(destinoFila.Id);
        lista.Remove(fila.Id);
        lista.Insert(destino, fila.Id);
        TrasMover(fila, destino, lista.Count);
    }

    private void TrasMover(Fila fila, int posicion, int total)
    {
        _aviso = Aviso.Ninguno;
        _asaPorEnfocar = fila.Clave;
        Anunciar(Textos["OrdenMenuAnuncioPosicion", Nombre(fila), posicion + 1, total].Value);
    }

    private void PulsarAsa(Fila fila)
    {
        if (EsCogida(fila))
        {
            Soltar(fila);
            return;
        }

        _cogida = fila;
        _copiaAntesDeCoger = [.. Lista(fila)];
        Anunciar(Textos["OrdenMenuAnuncioCogido", Nombre(fila)].Value);
    }

    private void Soltar(Fila fila)
    {
        _cogida = null;
        _copiaAntesDeCoger = null;
        var lista = Lista(fila);
        Anunciar(Textos["OrdenMenuAnuncioSoltado", Nombre(fila), lista.IndexOf(fila.Id) + 1, lista.Count].Value);
    }

    /// <summary>
    /// Con la fila cogida el asa se queda el teclado (preventDefault): las flechas la mueven,
    /// Espacio o Intro la sueltan y Escape la devuelve a donde estaba. Sin coger, la pulsación
    /// llega al botón y la recoge <see cref="PulsarAsa"/>.
    /// </summary>
    private void TeclaAsa(KeyboardEventArgs evento, Fila fila)
    {
        if (!EsCogida(fila))
            return;

        switch (evento.Key)
        {
            case "ArrowUp":
                Mover(fila, -1);
                break;
            case "ArrowDown":
                Mover(fila, 1);
                break;
            case " ":
            case "Enter":
            case "Tab":
                Soltar(fila);
                break;
            case "Escape":
                var lista = Lista(fila);
                lista.Clear();
                lista.AddRange(_copiaAntesDeCoger ?? []);
                _cogida = null;
                _copiaAntesDeCoger = null;
                _asaPorEnfocar = fila.Clave;
                Anunciar(Textos["OrdenMenuAnuncioCancelado", Nombre(fila)].Value);
                break;
        }
    }

    private void EmpezarArrastre(Fila fila) => _arrastre = fila;

    private void EntrarArrastre(Fila fila) =>
        _destinoArrastre = _arrastre is { } a && a.Tipo == fila.Tipo && a.GrupoId == fila.GrupoId ? fila.Clave : null;

    private void SoltarArrastre(Fila fila)
    {
        if (_arrastre is { } origen)
            MoverA(origen, fila);
        TerminarArrastre();
    }

    private void TerminarArrastre()
    {
        _arrastre = null;
        _destinoArrastre = null;
    }

    private void Anunciar(string texto) => _anuncio = texto;

    // ------------------------------------------------------------------ guardar

    private int CambiosSinGuardar => Diferencias(_grupos, _enlaces, _gruposGuardados, _enlacesGuardados);

    /// <summary>Posiciones que difieren: una por grupo fuera de sitio y una por enlace fuera de sitio en su grupo.</summary>
    private static int Diferencias(
        IReadOnlyList<string> grupos, IReadOnlyDictionary<string, List<string>> enlaces,
        IReadOnlyList<string> gruposReferencia, IReadOnlyDictionary<string, List<string>> enlacesReferencia)
    {
        var n = 0;
        for (var i = 0; i < grupos.Count; i++)
            if (i >= gruposReferencia.Count || gruposReferencia[i] != grupos[i])
                n++;

        foreach (var grupo in grupos)
        {
            var referencia = enlacesReferencia.GetValueOrDefault(grupo) ?? [];
            var lista = enlaces[grupo];
            for (var i = 0; i < lista.Count; i++)
                if (i >= referencia.Count || referencia[i] != lista[i])
                    n++;
        }

        return n;
    }

    /// <summary>
    /// «Qué cambió» del último guardado, medido frente al orden por defecto: el DTO no trae el orden
    /// anterior (ese queda en la auditoría, con el valor antiguo y el nuevo).
    /// </summary>
    private int DiferenciasConElOrdenPorDefecto()
    {
        var gruposPorDefecto = CatalogoMenuLateral.Grupos.Select(g => g.Id).ToList();
        var enlacesPorDefecto = CatalogoMenuLateral.Grupos.ToDictionary(
            g => g.Id,
            g => CatalogoMenuLateral.Enlaces.Where(e => e.GrupoId == g.Id).Select(e => e.Id).ToList());
        return Diferencias(_gruposGuardados, _enlacesGuardados, gruposPorDefecto, enlacesPorDefecto);
    }

    private Task GuardarAsync()
    {
        var enlaces = _grupos.SelectMany(g => _enlaces[g]).ToList();
        return EnviarAsync(new GuardarOrdenMenuLateralCommand([.. _grupos], enlaces, VersionEsperada), GuardarAsync);
    }

    /// <summary>Listas vacías = orden por defecto (contrato de <see cref="GuardarOrdenMenuLateralCommand"/>).</summary>
    private Task RestablecerAsync()
    {
        _confirmarRestablecer = false;
        return EnviarAsync(new GuardarOrdenMenuLateralCommand([], [], VersionEsperada), RestablecerAsync);
    }

    private Guid VersionEsperada => _orden?.Version ?? Guid.Empty;

    private async Task EnviarAsync(GuardarOrdenMenuLateralCommand comando, Func<Task> reintento)
    {
        if (_guardando)
            return;

        _guardando = true;
        _aviso = Aviso.Ninguno;
        _ultimaAccion = reintento;
        try
        {
            var resultado = await Mediator.Send(comando);
            if (resultado.EsExitoso)
            {
                _orden = await Mediator.Send(new ObtenerOrdenMenuLateralQuery());
                _autorOrden = await AutorDeAsync(_orden);
                AplicarGuardado();
                _aviso = Aviso.Guardado;
                Anunciar(Textos["OrdenMenuAvisoGuardadoTitulo"].Value);
            }
            else if (resultado.Error.Codigo == ConcurrenciaOptimista.CodigoConflicto)
            {
                // No se pisa el orden del otro Actor de Plataforma: se enseña quién y cuándo, y los
                // cambios propios siguen en pantalla hasta que se cargue el suyo.
                _ordenConflicto = await Mediator.Send(new ObtenerOrdenMenuLateralQuery());
                _autorConflicto = await AutorDeAsync(_ordenConflicto);
                _aviso = Aviso.Conflicto;
            }
            else if (resultado.Error.Codigo == CodigoSinPermiso)
            {
                // La concesión global se retiró a mitad de sesión: la página deja de ofrecer la edición.
                _esAdministradorPlataforma = false;
            }
            else
            {
                Logger.LogWarning("No se pudo guardar el orden del menú lateral: {Codigo}", resultado.Error.Codigo);
                _aviso = Aviso.Error;
            }
        }
        catch (Exception excepcion)
        {
            Logger.LogError(excepcion, "Error al guardar el orden del menú lateral");
            _aviso = Aviso.Error;
        }
        finally
        {
            _guardando = false;
        }
    }

    private Task ReintentarAsync() => _ultimaAccion?.Invoke() ?? Task.CompletedTask;

    private void CargarOrdenActual()
    {
        _orden = _ordenConflicto;
        _autorOrden = _autorConflicto;
        _ordenConflicto = null;
        _autorConflicto = null;
        AplicarGuardado();
        _aviso = Aviso.Ninguno;
    }

    // ------------------------------------------------------------------ vista previa

    private static readonly (string Rol, string Clave)[] RolesVistaPrevia =
    [
        (Roles.Administrador, "OrdenMenuRolAdministrador"),
        (Roles.DireccionCae, "OrdenMenuRolDireccionCae"),
        (Roles.CoordinadorCae, "OrdenMenuRolCoordinadorCae"),
        (Roles.GestorCae, "OrdenMenuRolGestorCae"),
        (Roles.Consulta, "OrdenMenuRolConsulta"),
    ];

    /// <summary>
    /// Mismo <see cref="CatalogoMenuLateral.Visibles"/> que el menú real, con una cuenta sintética
    /// de un solo rol: demuestra que ordenar no cambia qué ve cada rol.
    /// </summary>
    private ContextoMenuLateral ContextoVistaPrevia => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, _rol)], nameof(OrdenMenuLateral))),
        Vista: null,
        OpcionesComunicaciones.Value.Activo,
        EsAdministradorPlataforma: false,
        PerfilVocabularioTenant.Consultora,
        VariosTenants: false);

    private Previa VistaPrevia
    {
        get
        {
            var contexto = ContextoVistaPrevia;
            var grupos = CatalogoMenuLateral
                .Visibles(contexto, _grupos, _grupos.SelectMany(g => _enlaces[g]).ToList())
                .Select(v => new GrupoPrevio(v.Grupo.Id, v.Grupo.Titulo,
                    v.Enlaces.Select(e => new EnlacePrevio(e.Id, e.Icono, e.RotuloPara(contexto))).ToList()))
                .ToList();
            var vistos = grupos.Sum(g => g.Enlaces.Count);
            return new Previa(grupos, Textos["OrdenMenuPreviaTexto", vistos, CatalogoMenuLateral.Enlaces.Count].Value);
        }
    }

    // ------------------------------------------------------------------ formato

    private static string Fecha(DateTime utc)
    {
        var enUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = ZonaMadrid is null ? enUtc.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(enUtc, ZonaMadrid);
        return local.ToString("dd/MM/yyyy HH:mm");
    }

    private string TextoConflicto => _ordenConflicto is null
        ? Textos["OrdenMenuAvisoConflictoSinFila"].Value
        : Textos["OrdenMenuAvisoConflictoTexto", _autorConflicto ?? string.Empty, Fecha(_ordenConflicto.ActualizadoEnUtc)].Value;

    private string TextoQueCambio => DiferenciasConElOrdenPorDefecto() switch
    {
        0 => Textos["OrdenMenuQueCambioPorDefecto"].Value,
        var n => Textos["OrdenMenuQueCambioPosiciones", n].Value,
    };

    private string TituloEstado => CambiosSinGuardar switch
    {
        0 => Textos["OrdenMenuSinCambios"].Value,
        1 => Textos["OrdenMenuUnCambio"].Value,
        var n => Textos["OrdenMenuCambios", n].Value,
    };
}
