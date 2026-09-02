using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using AsignacionesFalsas = CaeManager.Application.Tests.Reportes;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;
using DocumentosFalsos = CaeManager.Application.Tests.Documentos;
using PlantillasFalsas = CaeManager.Application.Tests.Plantillas;
using TiposDocumentoFalsos = CaeManager.Application.Tests.TiposDocumento;
using TrabajadoresFalsos = CaeManager.Application.Tests.Trabajadores;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Arnés en memoria para <see cref="EjecutarImportacionCommandHandler"/>: monta
/// el handler con los fakes ya existentes de esta suite y expone tanto el estado
/// previo (lo que "ya está en la base") como los repositorios de escritura, para
/// poder comprobar las dos mitades del contrato de DCR-12 B — que la fila omitida
/// aparece en <c>Omitidos</c> con su motivo, y que **no se escribió nada** para ella.
///
/// Datos de prueba por variante (requerimiento global nº 1): cada causal de
/// descarte tiene su propio escenario construido aquí, no una fixture única
/// que las mezcle.
/// </summary>
internal sealed class EscenarioImportacion
{
    public const string DniConocido = "12345678Z";
    public const string DniDesconocido = "99999999R";
    public const string EmpresaConocida = "Ibertec S.A.";
    public const string EmpresaDesconocida = "Ibertec GmbH";
    public const string CentroConocido = "Cadena Industrial Iberia S.A. - Planta Norte";
    public const string CentroDeclaradoNoCreado = "Refrielectric - Planta Sur";
    public const string CentroNoDeclarado = "Centro que este archivo nunca menciona";
    public const string TipoDocumentoConocido = "Formación 20h";
    public const string TipoDocumentoInexistente = "Certificado inventado que no está en el catálogo";

    public DocumentosFalsos.EmpresasQueryContextFalso EmpresasContexto { get; } = new();
    public PlantillasFalsas.CentrosQueryContextFalso CentrosContexto { get; } = new();
    public PlantillasFalsas.TrabajadoresQueryContextFalso TrabajadoresContexto { get; } = new();
    public DocumentosFalsos.DocumentosQueryContextFalso DocumentosContexto { get; } = new();
    public TiposDocumentoFalsos.TiposDocumentoQueryContextFalso TiposDocumentoContexto { get; } = new();
    public AsignacionesFalsas.AsignacionesQueryContextFalso AsignacionesContexto { get; } = new();

    public ClientesFalsos.EmpresaRepositorioFalso EmpresaRepositorio { get; } = new();
    public TrabajadoresFalsos.TrabajadorRepositorioFalso TrabajadorRepositorio { get; } = new();
    public DocumentosFalsos.DocumentoRepositorioFalso DocumentoRepositorio { get; } = new();
    public Tests.Asignaciones.AsignacionRepositorioFalso AsignacionRepositorio { get; } = new();
    public ClientesFalsos.UnitOfWorkFalso UnitOfWork { get; } = new();

    public Trabajador? TrabajadorExistente { get; private set; }
    public Centro? CentroExistente { get; private set; }
    public TipoDocumento? TipoDocumentoExistente { get; private set; }

    /// <summary>Siembra la Empresa que las hojas Empleados/Extranjeros dan por buena.</summary>
    public EscenarioImportacion ConEmpresaExistente(string razonSocial = EmpresaConocida)
    {
        EmpresasContexto.ListaEmpresas.Add(new Empresa(razonSocial));
        return this;
    }

    /// <summary>Siembra un Trabajador ya dado de alta, para que su DNI sí se resuelva.</summary>
    public EscenarioImportacion ConTrabajadorExistente(string dni = DniConocido)
    {
        TrabajadorExistente = Trabajador.DeEmpresa(Guid.NewGuid(), "Marta", "Ruiz", dni);
        TrabajadoresContexto.ListaTrabajadores.Add(TrabajadorExistente);
        return this;
    }

    /// <summary>Siembra un Centro ya dado de alta, para que su nombre sí se resuelva.</summary>
    public EscenarioImportacion ConCentroExistente(string nombre = CentroConocido)
    {
        CentroExistente = new Centro(Guid.NewGuid(), Guid.NewGuid(), nombre);
        CentrosContexto.ListaCentros.Add(CentroExistente);
        return this;
    }

    public EscenarioImportacion ConTipoDocumentoExistente(string nombre = TipoDocumentoConocido)
    {
        TipoDocumentoExistente = new TipoDocumento(nombre, 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador);
        TiposDocumentoContexto.ListaTiposDocumento.Add(TipoDocumentoExistente);
        return this;
    }

    /// <summary>Siembra un Documento ya existente para el Trabajador y el Tipo sembrados.</summary>
    public EscenarioImportacion ConDocumentoExistente()
    {
        DocumentosContexto.ListaDocumentos.Add(Documento.DeTrabajador(
            TrabajadorExistente!.Id, TipoDocumentoExistente!.Id, new DateOnly(2026, 1, 1), null));
        return this;
    }

    /// <summary>Siembra una Asignación activa ya existente entre el Trabajador y el Centro sembrados.</summary>
    public EscenarioImportacion ConAsignacionActivaExistente()
    {
        AsignacionesContexto.ListaAsignaciones.Add(new Domain.Asignaciones.Asignacion(
            TrabajadorExistente!.Id, CentroExistente!.Id, new DateOnly(2026, 1, 1)));
        return this;
    }

    public EjecutarImportacionCommandHandler Handler() => new(
        EmpresaRepositorio, TrabajadorRepositorio, DocumentoRepositorio, AsignacionRepositorio,
        AsignacionesContexto, CentrosContexto, DocumentosContexto, EmpresasContexto,
        TiposDocumentoContexto, TrabajadoresContexto, UnitOfWork);

    public async Task<ResultadoImportacionDto> EjecutarAsync(PlanImportacionDto plan)
    {
        var resultado = await Handler().Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);
        return resultado.Valor;
    }

    /// <summary>Plan vacío al que cada escenario añade solo la fila de su causal.</summary>
    public static PlanImportacionDto Plan(
        IReadOnlyList<ClienteCentroImportadoDto>? clientesCentros = null,
        IReadOnlyList<EmpresaImportadaDto>? empresas = null,
        IReadOnlyList<TrabajadorImportadoDto>? trabajadores = null,
        IReadOnlyList<DocumentoImportadoDto>? documentos = null,
        IReadOnlyList<AsignacionImportadaDto>? asignaciones = null) =>
        new(clientesCentros ?? [], empresas ?? [], trabajadores ?? [], documentos ?? [], asignaciones ?? [], [], []);

    /// <summary>
    /// La fila de Centros_Plataformas que el archivo declara. Con
    /// <paramref name="yaExisteCentro"/> en <c>false</c> es el Centro que la
    /// importación no puede crear (Fase 10 exige Empresa); en <c>true</c>, el que
    /// el análisis vio existir y puede haber desaparecido antes de confirmar.
    /// </summary>
    public static ClienteCentroImportadoDto CentroDeclaradoEnElArchivo(
        string nombre = CentroDeclaradoNoCreado, bool yaExisteCentro = false) =>
        new(nombre, EsCritico: false, Direccion: null, Contacto: null, YaExisteCliente: false, YaExisteCentro: yaExisteCentro);
}
