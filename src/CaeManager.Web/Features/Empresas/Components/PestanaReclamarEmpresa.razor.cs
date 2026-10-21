using System.Globalization;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Components;

/// <summary>
/// Pestaña «Reclamar» de Empresa 360 (<c>?pestana=reclamar</c>), contra su maqueta «Reclamacion preventiva TALVEG.dc.html»
/// (decisión de Chris, 2026-10-19): la reclamación preventiva consolidada de una Empresa, con lo vencido, pendiente o por vencer de
/// la propia Empresa y de todos sus Trabajadores, filtrable por Delegación y Oficio (los filtros del servidor de
/// <see cref="ObtenerReclamacionPreventivaEmpresaQuery"/>, que es la única fuente) y por estado (en cliente, sobre lo ya devuelto).
/// «Sede» es aquí el nombre interno de la Delegación de Empresa (<c>DelegacionEmpresa</c>): no la Delegación de Tenant heredada.
///
/// <para>
/// No tiene comando ni correo propios: «Reclamar» abre el <c>DrawerReclamacionLote</c> de siempre con <c>LotesIniciales</c>, y cada
/// tarjeta se envía con el comando de siempre, que vuelve a validar documentos, cartera y destinatarios. Lo vigente en TALVEG que falta
/// en la plataforma del Cliente empresarial no se reclama a la Empresa: se enseña aparte, solo de lectura
/// (<see cref="ObtenerPendientesDeSubirAPlataformaEmpresaQuery"/>), y se gestiona en la pestaña Plataformas.
/// </para>
///
/// <para>
/// <b>Reglas de carrera:</b> cada carga lleva su versión y su propia señal de cancelación: arrancar una carga cancela la anterior (el
/// <c>DbContext</c> de la conexión no admite dos consultas a la vez), y una respuesta de una petición que ya no es la vigente (otro filtro,
/// un reintento, otra Empresa) se descarta. Los filtros que piden al servidor se deshabilitan mientras carga, y la retirada de la
/// pestaña cancela lo que viaja.
/// </para>
/// </summary>
public partial class PestanaReclamarEmpresa
{
    [Parameter, EditorRequired] public Guid EmpresaId { get; set; }

    private enum Categoria { Vencido, Pendiente, PorVencer }

    private enum Agrupacion { Sede, Trabajador, Tipo }

    private sealed record Fila(
        string Clave, string LoteClave, Guid? TrabajadorId, string? Trabajador, string Tipo, Categoria Categoria, string Detalle,
        TonoBadge Tono, string Insignia);

    private sealed record Ficha(Categoria Categoria, string Rotulo, int Total);

    private sealed record Grupo(string Clave, string Titulo, string Subtitulo, LoteReclamacionAgrupadoDto? Lote, IReadOnlyList<Fila> Filas);

    private sealed record FilaSubir(string Clave, string Tipo, string Titular, string Centro);

    private sealed record Vista(
        int EmpresaVencidosOPendientes, int TrabajadoresAfectados, IReadOnlyList<Ficha> Fichas,
        IReadOnlyList<Grupo> Grupos, string TextoRecuento, int TotalFilas)
    {
        public static readonly Vista Vacia = new(0, 0, [], [], string.Empty, 0);
    }

    private readonly CancellationTokenSource _ciclo = new();
    private CancellationTokenSource? _cargaActual;
    private ReclamacionPreventivaEmpresaDto? _dto;
    private IReadOnlyList<Fila> _filas = [];
    private IReadOnlyList<FilaSubir> _pendientesDeSubir = [];
    private Vista _vista = Vista.Vacia;

    private bool _error;
    private bool _errorSubir;
    private bool _recargando;
    private Guid _cargado;
    private int _carga;

    private Guid? _sedeId;
    private Guid? _oficioId;
    private Categoria? _estado;
    private Agrupacion? _agrupacion;
    private readonly HashSet<string> _abiertos = [];

    private bool _cajonVisible;
    private IReadOnlyList<LoteReclamacionAgrupadoDto>? _cajonLotes;

    private IReadOnlyList<OpcionFiltroDto> Sedes => _dto?.DelegacionesEmpresa ?? [];

    private bool HaySedes => Sedes.Count > 0;

    private bool HayFiltroDeServidor => _sedeId is not null || _oficioId is not null;

    private bool HayFiltros => HayFiltroDeServidor || _estado is not null;

    /// <summary>Sin Delegaciones no hay nada que agrupar por ellas: se agrupa por Trabajador.</summary>
    private Agrupacion AgrupacionActiva => _agrupacion switch
    {
        Agrupacion.Sede when !HaySedes => Agrupacion.Trabajador,
        { } elegida => elegida,
        _ => HaySedes ? Agrupacion.Sede : Agrupacion.Trabajador,
    };

    /// <summary>El cajón copia los lotes al abrirse: con una consulta en vuelo lo que se ve ya no es lo que se pidió, y no se ofrece reclamarlo.</summary>
    private bool PuedeReclamar => !_recargando && LotesDe(null).Any(l => l.Destinatarios is { Count: > 0 });

    /// <summary>La carga va aquí y no en <c>OnInitializedAsync</c>: navegar de una Empresa a otra reutiliza el componente.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (EmpresaId == _cargado)
            return;

        _cargado = EmpresaId;
        _dto = null;
        _filas = [];
        _pendientesDeSubir = [];
        _vista = Vista.Vacia;
        _sedeId = null;
        _oficioId = null;
        _estado = null;
        _agrupacion = null;
        _abiertos.Clear();
        _cajonVisible = false;
        _cajonLotes = null;
        await CargarAsync();
    }

    public void Dispose()
    {
        _ciclo.Cancel();
        _cargaActual?.Dispose();
        _ciclo.Dispose();
    }

    private async Task CargarAsync()
    {
        var carga = ++_carga;
        _cargaActual?.Cancel();
        _cargaActual?.Dispose();
        var propia = _cargaActual = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        var token = propia.Token;
        var empresaId = EmpresaId;
        var sedeId = _sedeId;
        var oficioId = _oficioId;
        _error = false;
        _errorSubir = false;
        _recargando = true;
        StateHasChanged();

        try
        {
            var dto = await Mediator.Send(new ObtenerReclamacionPreventivaEmpresaQuery(empresaId, sedeId, oficioId), token);
            if (carga != _carga)
                return;

            // Lo pendiente de subir es de lectura y secundario: si falla, la reclamación sigue siendo útil y se avisa, sin ocultarlo en silencio.
            IReadOnlyList<FilaSubir> pendientes = [];
            var errorSubir = false;
            try
            {
                var bloqueantes = await Mediator.Send(
                    new ObtenerPendientesDeSubirAPlataformaEmpresaQuery(empresaId, null, null, sedeId, oficioId), token);
                pendientes =
                [
                    .. bloqueantes.Select(b => new FilaSubir(
                        $"{b.CentroId}:{b.Ambito}:{b.TrabajadorId}:{b.TipoDocumentoId}", b.TipoDocumentoNombre,
                        b.Ambito == AmbitoAplicacion.Empresa ? Textos["DeLaEmpresa"] : b.TrabajadorNombre,
                        b.CentroNombre)),
                ];
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                errorSubir = true;
            }

            if (carga != _carga)
                return;

            _dto = dto;
            _pendientesDeSubir = pendientes;
            _errorSubir = errorSubir;
            _filas = ConstruirFilas(dto);
            Reconstruir();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (carga == _carga)
                _error = true;
        }
        finally
        {
            if (carga == _carga)
                _recargando = false;
        }
    }

    private IReadOnlyList<Fila> ConstruirFilas(ReclamacionPreventivaEmpresaDto dto)
    {
        var filas = new List<Fila>();
        foreach (var lote in dto.Lotes)
        {
            foreach (var documento in lote.Documentos)
            {
                // Lo vigente no se reclama (la nota al pie lo dice): ni se lista ni cuenta, para que lo que se ve no contradiga los recuentos.
                if (documento.Estado is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad)
                    continue;

                var categoria = documento.Estado == EstadoDocumento.Vencido ? Categoria.Vencido : Categoria.PorVencer;
                filas.Add(new Fila(
                    $"doc:{documento.DocumentoId}", lote.Clave, documento.TrabajadorId, documento.TrabajadorNombre, documento.TipoDocumentoNombre,
                    categoria, Textos["DocumentoVencimiento", documento.FechaVencimiento.ToString(CultureInfo.CurrentCulture)],
                    EstadoDocumentoUi.Tono(documento.Estado), EstadoDocumentoUi.Texto(documento.Estado)));
            }

            foreach (var pendiente in lote.PendientesSinFecha ?? [])
            {
                var sinConfirmar = pendiente.Motivo == MotivoPendienteDeReclamacion.SinConfirmar;
                filas.Add(new Fila(
                    pendiente.Clave, lote.Clave, pendiente.TrabajadorId, pendiente.TrabajadorNombre, pendiente.TipoDocumentoNombre,
                    Categoria.Pendiente, Textos[sinConfirmar ? "DetalleSinConfirmar" : "DetalleFalta"],
                    sinConfirmar ? TonoBadge.Advertencia : EstadoDocumentoUi.Tono(EstadoDocumento.Faltante),
                    sinConfirmar ? Textos["InsigniaSinConfirmar"] : EstadoDocumentoUi.Texto(EstadoDocumento.Faltante)));
            }
        }

        return [.. filas.OrderBy(f => f.Categoria).ThenBy(f => f.Tipo, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Recalcula lo que se pinta a partir de las filas y de los filtros de cliente (estado y agrupación).</summary>
    private void Reconstruir()
    {
        if (_dto is null)
        {
            _vista = Vista.Vacia;
            return;
        }

        var visibles = _estado is { } estado ? [.. _filas.Where(f => f.Categoria == estado)] : _filas;

        // Los Trabajadores se cuentan por Id, no por nombre: dos homónimos en una misma Empresa son dos personas.
        var trabajadores = visibles.Where(f => f.TrabajadorId is not null).Select(f => f.TrabajadorId).Distinct().Count();
        var recuento = Recuento(visibles.Count);
        if (trabajadores > 0)
            recuento += " · " + (trabajadores == 1 ? Textos["RecuentoTrabajadoresUno"] : Textos["RecuentoTrabajadoresVarios", trabajadores]);

        var deTrabajador = _filas.Where(f => f.TrabajadorId is not null).ToList();
        var fichas = new[]
        {
            new Ficha(Categoria.Vencido, Textos["TileVencidos"], deTrabajador.Count(f => f.Categoria == Categoria.Vencido)),
            new Ficha(Categoria.PorVencer, Textos["TilePorVencer"], deTrabajador.Count(f => f.Categoria == Categoria.PorVencer)),
            new Ficha(Categoria.Pendiente, Textos["TilePendientes"], deTrabajador.Count(f => f.Categoria == Categoria.Pendiente)),
        };

        _vista = new Vista(
            _filas.Count(f => f.TrabajadorId is null),
            deTrabajador.Select(f => f.TrabajadorId).Distinct().Count(),
            fichas, ConstruirGrupos(visibles), recuento, visibles.Count);
    }

    private IReadOnlyList<Grupo> ConstruirGrupos(IReadOnlyList<Fila> visibles)
    {
        if (visibles.Count == 0)
            return [];

        switch (AgrupacionActiva)
        {
            case Agrupacion.Sede:
                return
                [
                    .. _dto!.Lotes
                        .Select(lote => (Lote: lote, Filas: (IReadOnlyList<Fila>)[.. visibles.Where(f => f.LoteClave == lote.Clave)]))
                        .Where(x => x.Filas.Count > 0)
                        .Select(x => new Grupo(x.Lote.Clave, TituloDeLote(x.Lote), Recuento(x.Filas.Count), x.Lote, x.Filas)),
                ];

            case Agrupacion.Trabajador:
                var deEmpresa = visibles.Where(f => f.TrabajadorId is null).ToList();
                var porTrabajador = visibles.Where(f => f.TrabajadorId is not null)
                    .GroupBy(f => f.TrabajadorId!.Value)
                    .Select(g => (Id: g.Key, Nombre: g.First().Trabajador ?? string.Empty, Filas: g.ToList()))
                    .OrderBy(g => g.Nombre, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new Grupo($"trabajador:{g.Id}", g.Nombre, Recuento(g.Filas.Count), null, g.Filas));
                return
                [
                    .. deEmpresa.Count > 0 ? [new Grupo("empresa", Textos["GrupoEmpresa"], Recuento(deEmpresa.Count), null, deEmpresa)] : Array.Empty<Grupo>(),
                    .. porTrabajador,
                ];

            default:
                return
                [
                    .. visibles.GroupBy(f => f.Tipo)
                        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new Grupo($"tipo:{g.Key}", g.Key, Recuento(g.Count()), null, [.. g])),
                ];
        }
    }

    /// <summary>La sede a cuyo contacto va el lote; null si va a la agenda de la Empresa (o es el lote de la propia Empresa).</summary>
    private static string? NombreDeSede(LoteReclamacionAgrupadoDto lote) => lote.EmpresaEmpleadora?.DelegacionEmpresaNombre;

    private string TituloDeLote(LoteReclamacionAgrupadoDto lote) =>
        lote.EmpresaEmpleadora is null
            ? Textos["GrupoEmpresa"]
            : NombreDeSede(lote) is { } sede
                ? Textos["GrupoDelegacion", sede]
                : Textos["GrupoContactosEmpresa"];

    /// <summary>Con el grupo plegado la cabecera debe decir qué hay dentro: « · 1 vencido · 2 por vencer».</summary>
    private string DesgloseDeGrupo(Grupo grupo)
    {
        var partes = new List<string>();
        var vencidos = grupo.Filas.Count(f => f.Categoria == Categoria.Vencido);
        var pendientes = grupo.Filas.Count(f => f.Categoria == Categoria.Pendiente);
        var porVencer = grupo.Filas.Count(f => f.Categoria == Categoria.PorVencer);
        if (vencidos > 0)
            partes.Add(Textos["DesgloseVencidos", vencidos]);
        if (pendientes > 0)
            partes.Add(Textos["DesglosePendientes", pendientes]);
        if (porVencer > 0)
            partes.Add(Textos["DesglosePorVencer", porVencer]);

        return partes.Count == 0 ? string.Empty : " · " + string.Join(" · ", partes);
    }

    private string Recuento(int total) => total == 1 ? Textos["RecuentoDocumentosUno"] : Textos["RecuentoDocumentosVarios", total];

    private string TextoDestino(LoteReclamacionAgrupadoDto lote) =>
        Textos["DestinoA", string.Join(", ", (lote.Destinatarios ?? []).Select(d => d.Nombre))];

    /// <summary>El titular de cada fila se nombra salvo al agrupar por Trabajador, donde ya lo dice el grupo.</summary>
    private string DetalleDeFila(Fila fila)
    {
        if (AgrupacionActiva == Agrupacion.Trabajador)
            return fila.Detalle;

        return $"{fila.Trabajador ?? Textos["DeLaEmpresa"]} · {fila.Detalle}";
    }

    /// <summary>Un lote sin destinatario: sin contacto, o con un contacto de Delegación que el usuario no puede ver.</summary>
    private string TextoSinContacto(LoteReclamacionAgrupadoDto lote) =>
        Textos[NombreDeSede(lote) is null ? "DestinoSinContacto" : "DestinoDelegacionSinContacto"];

    private string TextoUltimaReclamacion(LoteReclamacionAgrupadoDto lote)
    {
        if (lote.UltimaReclamacionFechaUtc is not { } fecha)
            return Textos["SinReclamar"];

        var dias = (int)Math.Floor((DateTime.UtcNow - fecha).TotalDays);
        return dias switch
        {
            <= 0 => Textos["ReclamadoHoy"],
            1 => Textos["ReclamadoHaceUnDia"],
            _ => Textos["ReclamadoHaceDias", dias],
        };
    }

    private async Task CambiarSedeAsync(string? valor)
    {
        _sedeId = Guid.TryParse(valor, out var id) ? id : null;
        await CargarAsync();
    }

    private async Task CambiarOficioAsync(string? valor)
    {
        _oficioId = Guid.TryParse(valor, out var id) ? id : null;
        await CargarAsync();
    }

    private void CambiarEstado(string? valor)
    {
        _estado = Enum.TryParse<Categoria>(valor, out var categoria) ? categoria : null;
        Reconstruir();
    }

    private void AlternarEstado(Categoria categoria)
    {
        _estado = _estado == categoria ? null : categoria;
        Reconstruir();
    }

    /// <summary>Los grupos salen plegados (con 300 documentos la lista no se acaba): solo los que el usuario despliega están aquí.</summary>
    private void AlternarGrupo(string clave)
    {
        if (!_abiertos.Remove(clave))
            _abiertos.Add(clave);
    }

    private void Agrupar(Agrupacion agrupacion)
    {
        _agrupacion = agrupacion;
        Reconstruir();
    }

    private async Task QuitarFiltrosAsync()
    {
        var recargar = HayFiltroDeServidor;
        _estado = null;
        _sedeId = null;
        _oficioId = null;
        if (recargar)
            await CargarAsync();
        else
            Reconstruir();
    }

    /// <summary>
    /// Los lotes que se llevan al cajón: los de la consulta con solo las filas que se ven (el filtro de estado incluido), todos o los de un
    /// grupo. El cajón no amplía nada: cada tarjeta se envía con el comando de siempre, que vuelve a validarla. Como al quitar documentos en
    /// el propio cajón, los contactos sugeridos se resolvieron sobre todo el lote: si el subconjunto cambia quién es dueño de qué, el envío
    /// se rechaza (nunca llega a un contacto equivocado) y pide actualizar la vista.
    /// </summary>
    private IReadOnlyList<LoteReclamacionAgrupadoDto> LotesDe(string? claveLote)
    {
        if (_dto is null)
            return [];

        var claves = (_estado is { } estado ? _filas.Where(f => f.Categoria == estado) : _filas).Select(f => f.Clave).ToHashSet();
        var lotes = new List<LoteReclamacionAgrupadoDto>();
        foreach (var lote in _dto.Lotes.Where(l => claveLote is null || l.Clave == claveLote))
        {
            var documentos = lote.Documentos.Where(d => claves.Contains($"doc:{d.DocumentoId}")).ToList();
            var pendientes = (lote.PendientesSinFecha ?? []).Where(p => claves.Contains(p.Clave)).ToList();
            if (documentos.Count > 0 || pendientes.Count > 0)
                lotes.Add(lote with { Documentos = documentos, PendientesSinFecha = pendientes });
        }

        return lotes;
    }

    private void AbrirTodo() => AbrirCajon(LotesDe(null));

    private void AbrirGrupo(string claveLote) => AbrirCajon(LotesDe(claveLote));

    private void AbrirCajon(IReadOnlyList<LoteReclamacionAgrupadoDto> lotes)
    {
        if (_recargando || lotes.Count == 0)
            return;

        _cajonLotes = lotes;
        _cajonVisible = true;
    }

    private void CambiarCajon(bool visible) => _cajonVisible = visible;
}
