using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Domain.Common;
using CaeManager.Domain.RelacionesEmpresariales;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;
using DocumentosFalsos = CaeManager.Application.Tests.Documentos;
using PlantillasFalsas = CaeManager.Application.Tests.Plantillas;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Arnés en memoria para <see cref="EjecutarImportacionCombinadaCommandHandler"/>, con el repositorio
/// de operaciones falso de esta suite: observa las dos mitades del contrato —qué se escribió y qué
/// se reportó— sin PostgreSQL. La carrera concurrente real exige la base y vive en IntegrationTests.
/// </summary>
internal sealed class EscenarioImportacionCombinada
{
    public EmpresaRepositorioConFallo EmpresaRepositorio { get; } = new();
    public OperacionImportacionRepositorioFalso OperacionImportacionRepositorio { get; } = new();
    public CaeManager.Application.Tests.Trabajadores.TrabajadorRepositorioFalso TrabajadorRepositorio { get; } = new();
    public CaeManager.Application.Tests.Centros.CentroRepositorioFalso CentroRepositorio { get; } = new();
    public PlantillasFalsas.CentrosQueryContextFalso CentrosContexto { get; } = new();
    public PlantillasFalsas.TrabajadoresQueryContextFalso TrabajadoresContexto { get; } = new();

    /// <summary>Relaciones Empresariales que "ya están en la base" (la consulta de asociaciones actuales las lee).</summary>
    public List<RelacionEmpresarial> RelacionesExistentes { get; } = [];

    /// <summary>Doble de relaciones: por defecto cualquier llamada de escritura es un error del test; con <c>Permitir</c> las registra.</summary>
    public RelacionesRegistradas Relaciones { get; } = new();

    public EjecutarImportacionCombinadaCommandHandler Handler(
        IAlcanceDatosService? alcance = null, AutoridadCargaMasiva autoridad = AutoridadCargaMasiva.Total)
    {
        // Lo que "ya está en la base" en cada ejecución: se relee del repositorio, como haría una segunda lectura real.
        var empresas = new DocumentosFalsos.EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange(EmpresaRepositorio.Empresas
            .Where(e => e.TenantId == EmpresaRepositorio.TenantPropietarioId && !e.EstaEliminado));
        empresas.ListaRelacionesEmpresariales.AddRange(RelacionesExistentes);

        return new EjecutarImportacionCombinadaCommandHandler(
            EmpresaRepositorio, Relaciones,
            CentroRepositorio,
            TrabajadorRepositorio,
            CentrosContexto, empresas, TrabajadoresContexto,
            OperacionImportacionRepositorio, new AdministracionPermitida(autoridad), alcance ?? new ClientesFalsos.AlcanceDatosServiceFalso());
    }

    public async Task<ResultadoImportacionCombinadaDto> EjecutarAsync(
        PlanImportacionCombinadaDto plan, bool reemplazar = false, IAlcanceDatosService? alcance = null,
        AutoridadCargaMasiva autoridad = AutoridadCargaMasiva.Total)
    {
        var resultado = await Handler(alcance, autoridad).Handle(new EjecutarImportacionCombinadaCommand(plan, reemplazar), CancellationToken.None);
        return resultado.Valor;
    }

    public static PlanImportacionCombinadaDto Plan(
        Guid? operacionId = null,
        IReadOnlyList<ClienteImportadoDto>? clientes = null,
        IReadOnlyList<EmpresaCombinadaImportadaDto>? empresas = null,
        IReadOnlyList<CentroImportadoDto>? centros = null,
        IReadOnlyList<TrabajadorImportadoDto>? trabajadores = null) =>
        new(operacionId ?? Guid.NewGuid(), clientes ?? [], empresas ?? [], centros ?? [], trabajadores ?? [], [], []);

    /// <summary>Repositorio de Empresas que, si se le pide, falla al agregar: un fallo a mitad de construir el plan.</summary>
    internal sealed class EmpresaRepositorioConFallo : ClientesFalsos.EmpresaRepositorioFalso, CaeManager.Domain.Empresas.IEmpresaRepository
    {
        public Exception? ExcepcionAlAgregar { get; set; }

        void CaeManager.Domain.Empresas.IEmpresaRepository.Agregar(CaeManager.Domain.Empresas.Empresa empresa)
        {
            if (ExcepcionAlAgregar is not null) throw ExcepcionAlAgregar;
            Agregar(empresa);
        }
    }

    private sealed class AdministracionPermitida(AutoridadCargaMasiva autoridad = AutoridadCargaMasiva.Total) : IAutorizacionEscrituraEfectiva
    {
        public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default) => Task.FromResult(autoridad);
    }

    /// <summary>
    /// Por defecto, ningún plan de estos tests asocia Empresas con Clientes: cualquier llamada de escritura sería un
    /// error del test. Con <see cref="Permitir"/> se registran las cierres y altas para poder comprobarlas.
    /// </summary>
    internal sealed class RelacionesRegistradas : IRelacionEmpresarialRepository
    {
        public bool Permitir { get; set; }
        public List<(Guid ProveedoraId, Guid ClienteId)> Cerradas { get; } = [];
        public List<(Guid ProveedoraId, Guid ClienteId)> Agregadas { get; } = [];

        public void Agregar(RelacionEmpresarial relacion) => throw new NotImplementedException();
        public Task<bool> AgregarSiNoVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default)
        {
            if (!Permitir) throw new NotImplementedException();
            Agregadas.Add((proveedoraId, clienteId));
            return Task.FromResult(true);
        }

        public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default)
        {
            if (!Permitir) throw new NotImplementedException();
            Cerradas.Add((proveedoraId, clienteId));
            return Task.FromResult(true);
        }

        public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) => Task.FromResult(ContrapartesVigentes.Vacias);
        public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
