using System.Linq.Expressions;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

/// <summary>
/// Los filtros de vista del listado de Documentos: todo lo que decide QUÉ Documentos se listan, sin el orden ni la
/// paginación. Los llevan igual <see cref="ObtenerDocumentosQuery"/> y
/// <see cref="ObtenerResumenDocumentosPorTipo.ObtenerResumenDocumentosPorTipoQuery"/>, y los dos se resuelven con
/// <see cref="DocumentosFiltrados.ComponerAsync"/>: un filtro nuevo se añade aquí y a las dos consultas
/// (<c>FiltrosDeDocumentosTests</c> pone en rojo la que se quede atrás).
/// </summary>
internal sealed record FiltrosDeDocumentos(
    Guid? TrabajadorId,
    AmbitoAplicacion? Ambito,
    string? Busqueda,
    EstadoDocumento? Estado,
    IReadOnlyCollection<EstadoDocumento>? Estados,
    Guid? PropietarioId,
    DateOnly? FechaVencimientoHasta,
    Guid? DocumentoId,
    Guid? TipoDocumentoId,
    Guid? ProveedorPlataformaCaeId);

/// <summary>
/// Las fuentes de lectura con las que se compone <see cref="DocumentosFiltrados"/>. Las inyecta cada handler y las
/// pasa aquí: así cada uno sigue declarando en su constructor de qué persistencia depende.
/// </summary>
internal sealed record FuentesDeDocumentos(
    IConfiguracionQueryContext Configuracion,
    IDocumentosQueryContext Documentos,
    IEmpresasQueryContext Empresas,
    IProyectosQueryContext Proyectos,
    ITiposDocumentoQueryContext TiposDocumento,
    ITrabajadoresQueryContext Trabajadores,
    IVehiculosQueryContext Vehiculos,
    IAlcanceDatosService AlcanceDatos,
    ICentrosQueryContext Centros);

/// <summary>Una fila de la unión de Documentos de los cinco ámbitos, antes de proyectarla a un DTO.</summary>
internal sealed class FilaDocumento
{
    public Guid Id { get; init; }
    public AmbitoAplicacion Ambito { get; init; }
    public Guid? TrabajadorId { get; init; }
    public Guid? PropietarioId { get; init; }
    public string PropietarioNombre { get; init; } = string.Empty;
    public string TipoDocumentoNombre { get; init; } = string.Empty;
    public Guid TipoDocumentoId { get; init; }
    public DateOnly FechaEmision { get; init; }
    public EstadoVigenciaDocumento EstadoVigencia { get; init; }
    public DateOnly? FechaVencimiento { get; init; }
    public string? ArchivoUrl { get; init; }
}

/// <summary>
/// La consulta de Documentos que el usuario puede listar con unos filtros de vista, sin ejecutar: el punto único
/// del que salen la lista paginada (<see cref="ObtenerDocumentosQueryHandler"/>) y el resumen por Tipo de documento
/// de la lista agrupada (<c>ObtenerResumenDocumentosPorTipoQueryHandler</c>). Que los dos partan de aquí es lo que
/// garantiza que la cabecera de un grupo cuenta exactamente los Documentos que se listan al abrirlo: mismo alcance,
/// mismos filtros y misma clave de estado.
///
/// <para>
/// Une los Documentos operativos de Trabajador, Cliente empresarial, Empresa, Vehículo y Proyecto (viven en la misma
/// tabla, cada uno con un único FK de propietario relleno): cada ámbito hace su propio join al propietario que le
/// corresponde y se acota a lo que el alcance del usuario deja ver, y las cinco ramas se combinan con
/// <c>Concat</c> en vez de un LEFT JOIN quíntuple. Los filtros solo recortan esa unión: nunca la amplían.
/// </para>
///
/// <para>
/// El estado que se PINTA lo sigue calculando <see cref="CalculadoraEstadoDocumento"/> en memoria, fuente única de
/// verdad; lo que aquí se lleva a SQL es <see cref="ClaveDeEstado"/>, su traducción a un rango de fechas, para
/// filtrar, ordenar y contar sin materializar los Documentos del Tenant.
/// <c>DocumentosPaginacionEnSqlTests</c> y <c>CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests</c> comparan las
/// dos para que no se separen en silencio.
/// </para>
/// </summary>
internal sealed class DocumentosFiltrados
{
    private DocumentosFiltrados(
        IQueryable<FilaDocumento> sinFiltroDeEstado,
        IQueryable<FilaDocumento> filtrados,
        Expression<Func<FilaDocumento, int>> claveDeEstado,
        DateOnly hoy,
        int umbralAmbarDias,
        int umbralRojoDias)
    {
        SinFiltroDeEstado = sinFiltroDeEstado;
        Filtrados = filtrados;
        ClaveDeEstado = claveDeEstado;
        Hoy = hoy;
        UmbralAmbarDias = umbralAmbarDias;
        UmbralRojoDias = umbralRojoDias;
    }

    /// <summary>
    /// Los Documentos con el alcance y todos los filtros MENOS el de estado: de aquí sale la franja de estado del
    /// listado, donde cada cifra dice cuántos quedarían al marcar solo ese estado.
    /// </summary>
    public IQueryable<FilaDocumento> SinFiltroDeEstado { get; }

    /// <summary>Los Documentos que se listan: alcance y todos los filtros, también el de estado.</summary>
    public IQueryable<FilaDocumento> Filtrados { get; }

    /// <summary>
    /// La clave de estado de un Documento, en SQL: el orden de <see cref="EstadoDocumentalFiltro.ClaveOrden"/>
    /// (0 Vencido, 1 Urgente, 2 Próximo, 3 Sin confirmar, 4 Vigente, 5 Sin caducidad). Es la única expresión con la
    /// que se filtra, se ordena y se cuenta por estado: así las tres operaciones parten los Documentos igual.
    /// </summary>
    public Expression<Func<FilaDocumento, int>> ClaveDeEstado { get; }

    /// <summary>El día de negocio con el que se calcularon los umbrales: el mismo para SQL y para la calculadora.</summary>
    public DateOnly Hoy { get; }

    public int UmbralAmbarDias { get; }

    public int UmbralRojoDias { get; }

    public static async Task<DocumentosFiltrados> ComponerAsync(
        FuentesDeDocumentos fuentes, FiltrosDeDocumentos filtros, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await fuentes.AlcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await fuentes.AlcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await fuentes.AlcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await fuentes.AlcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in fuentes.Documentos.Documentos.Operativos()
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in fuentes.Trabajadores.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in fuentes.TiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumento
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Trabajador,
                TrabajadorId = documento.TrabajadorId,
                PropietarioId = documento.TrabajadorId,
                PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deCliente =
            from documento in fuentes.Documentos.Documentos.Operativos()
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            // Documento.ClienteId ya apunta a Empresas (F3): el propietario "Cliente"
            // de un Documento se resuelve contra Empresas, no contra Clientes (congelada).
            join cliente in fuentes.Empresas.Empresas on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in fuentes.TiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumento
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Cliente,
                TrabajadorId = null,
                PropietarioId = documento.ClienteId,
                PropietarioNombre = cliente.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deEmpresa =
            from documento in fuentes.Documentos.Documentos.Operativos()
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in fuentes.Empresas.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in fuentes.TiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumento
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Empresa,
                TrabajadorId = null,
                PropietarioId = documento.EmpresaId,
                PropietarioNombre = empresa.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deVehiculo =
            from documento in fuentes.Documentos.Documentos.Operativos()
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in fuentes.Vehiculos.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in fuentes.TiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumento
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Vehiculo,
                TrabajadorId = null,
                PropietarioId = documento.VehiculoId,
                PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")",
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deProyecto =
            from documento in fuentes.Documentos.Documentos.Operativos()
            where documento.ProyectoId != null
            join proyecto in fuentes.Proyectos.Proyectos on documento.ProyectoId!.Value equals proyecto.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(proyecto.ClienteId)
            join tipoDocumento in fuentes.TiposDocumento.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumento
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Proyecto,
                TrabajadorId = null,
                PropietarioId = documento.ProyectoId,
                PropietarioNombre = proyecto.Nombre,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var consulta = deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo).Concat(deProyecto);

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Las cinco ramas de arriba ya están acotadas al alcance: solo estrecha.
        if (filtros.DocumentoId is not null)
            consulta = consulta.Where(x => x.Id == filtros.DocumentoId);

        if (filtros.TrabajadorId is not null)
            consulta = consulta.Where(x => x.TrabajadorId == filtros.TrabajadorId);

        if (filtros.Ambito is not null)
            consulta = consulta.Where(x => x.Ambito == filtros.Ambito);

        if (filtros.PropietarioId is not null)
            consulta = consulta.Where(x => x.PropietarioId == filtros.PropietarioId);

        // Los dos filtros recortan la unión ya limitada al alcance del usuario: nunca la amplían.
        if (filtros.TipoDocumentoId is { } tipoDocumentoId)
            consulta = consulta.Where(x => x.TipoDocumentoId == tipoDocumentoId);

        if (filtros.ProveedorPlataformaCaeId is { } proveedorPlataformaCaeId)
        {
            var documentosEnPlataforma =
                from acreditacion in fuentes.Documentos.AcreditacionesDocumentoPlataforma
                join canal in fuentes.Centros.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
                where canal.ProveedorPlataformaCaeId == proveedorPlataformaCaeId
                select acreditacion.DocumentoId;
            consulta = consulta.Where(x => documentosEnPlataforma.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(filtros.Busqueda))
        {
            var busqueda = filtros.Busqueda;
            consulta = consulta.Where(x =>
                TextoDeBusqueda.Contiene(x.PropietarioNombre, busqueda) ||
                TextoDeBusqueda.Contiene(x.TipoDocumentoNombre, busqueda));
        }

        var parametros = await fuentes.Configuracion.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Equivalencias con CalculadoraEstadoDocumento, que compara "días
        // restantes" contra los umbrales: días <= umbral es lo mismo que
        // fechaVencimiento <= hoy + umbral.
        var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
        var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

        // Ventana temporal fija elegida por quien llama, distinta del filtro de estado (ver
        // ObtenerDocumentosQuery.FechaVencimientoHasta): un vencido no entra nunca.
        if (filtros.FechaVencimientoHasta is { } hasta)
            consulta = consulta.Where(x => x.FechaVencimiento != null && x.FechaVencimiento >= hoy && x.FechaVencimiento <= hasta);

        // «No caduca» y «sin confirmar» se distinguen por EstadoVigencia, nunca por una fecha nula. Si alguna vez
        // cambia la calculadora, esta clave cambia con ella.
        Expression<Func<FilaDocumento, int>> claveDeEstado = x =>
            x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
            : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
            : x.FechaVencimiento < hoy ? 0
            : x.FechaVencimiento <= limiteRojo ? 1
            : x.FechaVencimiento <= limiteAmbar ? 2
            : 4;

        var sinFiltroDeEstado = consulta;

        var estadosPedidos = (filtros.Estados ?? []).ToList();
        if (filtros.Estado is { } unEstado && !estadosPedidos.Contains(unEstado))
            estadosPedidos.Add(unEstado);

        if (estadosPedidos.Count > 0)
        {
            // «En tolerancia» es un estado de contexto de Centro (VigenciaEnCentro) y Faltante solo lo emiten las
            // Alertas: esta lista nunca los produce, así que su clave (6) no coincide con ninguna fila. Filtrar
            // solo por ellos no devuelve nada (y no «todo», que sacaría los vencidos con otro rótulo).
            var clavesPedidas = estadosPedidos.Select(estado => EstadoDocumentalFiltro.ClaveOrden(estado)).Distinct().ToList();
            consulta = consulta.Where(claveDeEstado.Con((FilaDocumento _, int clave) => clavesPedidas.Contains(clave)));
        }

        return new DocumentosFiltrados(
            sinFiltroDeEstado, consulta, claveDeEstado, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
    }
}

/// <summary>
/// Composición de expresiones para EF: permite escribir una clave calculada en SQL una sola vez y usarla dentro de
/// otra expresión (un filtro, la clave de un <c>GroupBy</c>) sin copiarla.
/// </summary>
internal static class ComposicionDeExpresiones
{
    /// <summary>
    /// <c>x =&gt; exterior(x, interior(x))</c>, con el cuerpo de <paramref name="interior"/> incrustado en el de
    /// <paramref name="exterior"/>: EF recibe una sola expresión traducible, igual que si se hubiera escrito a mano.
    /// </summary>
    public static Expression<Func<T, TResultado>> Con<T, TClave, TResultado>(
        this Expression<Func<T, TClave>> interior, Expression<Func<T, TClave, TResultado>> exterior)
    {
        var cuerpo = new Sustitucion(exterior.Parameters[0], interior.Parameters[0], exterior.Parameters[1], interior.Body)
            .Visit(exterior.Body);
        return Expression.Lambda<Func<T, TResultado>>(cuerpo, interior.Parameters);
    }

    private sealed class Sustitucion(
        ParameterExpression fila, Expression filaNueva, ParameterExpression clave, Expression claveNueva) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == fila ? filaNueva : node == clave ? claveNueva : base.VisitParameter(node);
    }
}
