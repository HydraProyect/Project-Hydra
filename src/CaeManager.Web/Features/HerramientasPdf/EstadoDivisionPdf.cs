using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.HerramientasPdf;

/// <summary>Quién marcó un corte: una propuesta de la detección sin confirmar o el propio usuario.</summary>
public enum OrigenCorte
{
    Sugerido,
    Usuario,
}

/// <summary>Lo que aún le falta a un tramo para poder darse de alta como Documento.</summary>
public enum DatoPendiente
{
    Titular,
    Tipo,
    FechaEmision,
}

public sealed record RangoPaginas(int Inicial, int Final)
{
    public int Paginas => Final - Inicial + 1;
    public string Clave => $"{Inicial}-{Final}";
}

/// <summary>
/// Lo que el usuario ha decidido sobre un tramo. <see cref="EmpresaDelTitularId"/> es solo la coordenada del selector
/// «Empresa a la que pertenece»: no autoriza nada, el orquestador comprueba el titular contra el catálogo.
/// </summary>
public sealed class AsignacionTramo(TramoDetectado? sugerencia)
{
    public TramoDetectado? Sugerencia { get; } = sugerencia;
    public ClaseTitular? Clase { get; set; }
    public Guid? EmpresaDelTitularId { get; set; }
    public Guid? TitularId { get; set; }
    public Guid? TipoDocumentoId { get; set; }
    public DateOnly? FechaEmision { get; set; }
    public DateOnly? FechaCaducidad { get; set; }
    public bool SinCaducidad { get; set; }
    public bool Omitido { get; set; }

    /// <summary>
    /// Documento exige exactamente un propietario, un tipo y una fecha de emisión: lo que no los tenga no se crea. No hay
    /// alta «sin asignar» porque el modelo no la admite.
    /// </summary>
    public IReadOnlyList<DatoPendiente> Pendientes()
    {
        var pendientes = new List<DatoPendiente>(3);
        if (Clase is null || TitularId is null)
            pendientes.Add(DatoPendiente.Titular);
        if (TipoDocumentoId is null)
            pendientes.Add(DatoPendiente.Tipo);
        if (FechaEmision is null)
            pendientes.Add(DatoPendiente.FechaEmision);
        return pendientes;
    }

    public bool ListaParaCrear => !Omitido && Pendientes().Count == 0;

    /// <summary>Tipo y titular siguen siendo los que propuso la detección.</summary>
    public bool CoincideConSugerencia =>
        Sugerencia is { TipoDocumentoId: not null, TitularId: not null }
        && Sugerencia.TipoDocumentoId == TipoDocumentoId && Sugerencia.TitularId == TitularId && Sugerencia.Clase == Clase;
}

/// <summary>
/// Estado del asistente de Dividir, sin nada de interfaz: los cortes y lo asignado a cada tramo. Las asignaciones se
/// guardan por rango y no por posición, así que volver atrás, mover un corte y deshacerlo no pierde nada de lo escrito.
/// Los tramos detectados son solo sugerencias: nada de aquí crea un Documento.
/// </summary>
public sealed class EstadoDivisionPdf
{
    private readonly SortedDictionary<int, OrigenCorte> _cortes = [];
    private readonly Dictionary<string, AsignacionTramo> _asignaciones = [];
    private readonly IReadOnlyList<TramoDetectado> _detectados;

    public EstadoDivisionPdf(int numeroPaginas, IReadOnlyList<TramoDetectado> detectados, CatalogoDivisionPdf catalogo)
    {
        NumeroPaginas = numeroPaginas;
        Catalogo = catalogo;
        _detectados = detectados;

        foreach (var tramo in detectados.Where(t => t.PaginaFinal < numeroPaginas))
            _cortes[tramo.PaginaFinal] = OrigenCorte.Sugerido;
    }

    public int NumeroPaginas { get; }
    public CatalogoDivisionPdf Catalogo { get; }

    public int PropuestasSinConfirmar => _cortes.Values.Count(o => o == OrigenCorte.Sugerido);

    /// <summary>Corte tras la página indicada, o null si no hay.</summary>
    public OrigenCorte? CorteTras(int pagina) => _cortes.TryGetValue(pagina, out var origen) ? origen : null;

    public void AlternarCorte(int pagina)
    {
        if (pagina < 1 || pagina >= NumeroPaginas)
            return;

        if (!_cortes.Remove(pagina))
            _cortes[pagina] = OrigenCorte.Usuario;
    }

    public void ConfirmarPropuestas()
    {
        foreach (var pagina in _cortes.Keys.ToList())
            _cortes[pagina] = OrigenCorte.Usuario;
    }

    public IReadOnlyList<RangoPaginas> Rangos
    {
        get
        {
            var rangos = new List<RangoPaginas>(_cortes.Count + 1);
            var inicial = 1;
            foreach (var pagina in _cortes.Keys)
            {
                rangos.Add(new RangoPaginas(inicial, pagina));
                inicial = pagina + 1;
            }

            rangos.Add(new RangoPaginas(inicial, NumeroPaginas));
            return rangos;
        }
    }

    /// <summary>La de ese rango exacto; si el usuario ha movido los cortes y el rango es nuevo, nace sin sugerencia.</summary>
    public AsignacionTramo Asignacion(RangoPaginas rango)
    {
        if (_asignaciones.TryGetValue(rango.Clave, out var existente))
            return existente;

        var sugerencia = _detectados.FirstOrDefault(t => t.PaginaInicial == rango.Inicial && t.PaginaFinal == rango.Final);
        var asignacion = new AsignacionTramo(sugerencia);
        VolverASugerencia(asignacion);
        _asignaciones[rango.Clave] = asignacion;
        return asignacion;
    }

    public void VolverASugerencia(AsignacionTramo asignacion)
    {
        var sugerencia = asignacion.Sugerencia;
        asignacion.Clase = sugerencia?.Clase;
        asignacion.TitularId = sugerencia?.TitularId;
        asignacion.TipoDocumentoId = sugerencia?.TipoDocumentoId;
        asignacion.EmpresaDelTitularId = sugerencia is { Clase: { } clase, TitularId: { } titular }
            ? Catalogo.EmpresaDe(clase, titular)
            : Catalogo.EmpresaGestionada.Id;
    }

    /// <summary>Al cambiar «Es de», un tipo de otro ámbito deja de valer: se quita en vez de dejar un alta que se rechazaría.</summary>
    public void ElegirClase(AsignacionTramo asignacion, ClaseTitular clase)
    {
        asignacion.Clase = clase;
        asignacion.TitularId = clase == ClaseTitular.EmpresaGestionada ? Catalogo.EmpresaGestionada.Id : null;
        asignacion.EmpresaDelTitularId = Catalogo.EmpresaGestionada.Id;

        if (!TiposPara(clase).Any(t => t.Id == asignacion.TipoDocumentoId))
            asignacion.TipoDocumentoId = null;
    }

    public void ElegirEmpresaDelTitular(AsignacionTramo asignacion, Guid? empresaId)
    {
        asignacion.EmpresaDelTitularId = empresaId;
        asignacion.TitularId = null;
    }

    public IEnumerable<EmpresaDivisionPdf> EmpresasConTitulares =>
        new[] { Catalogo.EmpresaGestionada }.Concat(Catalogo.Subcontratas);

    public IEnumerable<TrabajadorDivisionPdf> TrabajadoresDe(Guid? empresaId) =>
        Catalogo.Trabajadores.Where(t => t.EmpresaDuenaId == empresaId);

    public IEnumerable<VehiculoDivisionPdf> VehiculosDe(Guid? empresaId) =>
        Catalogo.Vehiculos.Where(v => v.EmpresaDuenaId == empresaId);

    public IEnumerable<TipoDocumentoDivisionPdf> TiposPara(ClaseTitular? clase) =>
        Catalogo.Tipos.Where(t => clase switch
        {
            ClaseTitular.EmpresaGestionada or ClaseTitular.Subcontrata => t.Ambito == AmbitoAplicacion.Empresa,
            ClaseTitular.Trabajador => t.Ambito == AmbitoAplicacion.Trabajador,
            ClaseTitular.Vehiculo => t.Ambito == AmbitoAplicacion.Vehiculo,
            _ => t.Ambito is AmbitoAplicacion.Empresa or AmbitoAplicacion.Trabajador or AmbitoAplicacion.Vehiculo,
        });

    public TipoDocumentoDivisionPdf? Tipo(Guid? tipoId) => Catalogo.Tipos.FirstOrDefault(t => t.Id == tipoId);

    public string? NombreTitular(AsignacionTramo asignacion) => (asignacion.Clase, asignacion.TitularId) switch
    {
        (ClaseTitular.EmpresaGestionada, not null) => Catalogo.EmpresaGestionada.RazonSocial,
        (ClaseTitular.Subcontrata, { } id) => Catalogo.Subcontratas.FirstOrDefault(s => s.Id == id)?.RazonSocial,
        (ClaseTitular.Trabajador, { } id) => Catalogo.Trabajadores.FirstOrDefault(t => t.Id == id)?.NombreCompleto,
        (ClaseTitular.Vehiculo, { } id) => Catalogo.Vehiculos.FirstOrDefault(v => v.Id == id)?.NumeroPlaca,
        _ => null,
    };

    /// <summary>Solo lo que tiene todos sus datos y no se ha omitido, que es lo único que llega al orquestador.</summary>
    public IReadOnlyList<TramoACrear> TramosACrear() =>
        Rangos
            .Select(rango => (Rango: rango, Asignacion: Asignacion(rango)))
            .Where(t => t.Asignacion.ListaParaCrear)
            .Select(t => new TramoACrear(
                t.Rango.Inicial, t.Rango.Final, t.Asignacion.Clase!.Value, t.Asignacion.TitularId!.Value,
                t.Asignacion.TipoDocumentoId!.Value, t.Asignacion.FechaEmision!.Value,
                t.Asignacion.SinCaducidad ? null : t.Asignacion.FechaCaducidad, t.Asignacion.SinCaducidad))
            .ToList();
}
