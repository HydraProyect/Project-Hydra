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

    public EjecutarImportacionCombinadaCommandHandler Handler()
    {
        // Lo que "ya está en la base" en cada ejecución: se relee del repositorio, como haría una segunda lectura real.
        var empresas = new DocumentosFalsos.EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange(EmpresaRepositorio.Empresas
            .Where(e => e.TenantId == EmpresaRepositorio.TenantPropietarioId && !e.EstaEliminado));

        return new EjecutarImportacionCombinadaCommandHandler(
            EmpresaRepositorio, new RelacionesNoUsadas(),
            new CaeManager.Application.Tests.Centros.CentroRepositorioFalso(),
            TrabajadorRepositorio,
            new PlantillasFalsas.CentrosQueryContextFalso(), empresas, new PlantillasFalsas.TrabajadoresQueryContextFalso(),
            OperacionImportacionRepositorio, new AdministracionPermitida());
    }

    public async Task<ResultadoImportacionCombinadaDto> EjecutarAsync(PlanImportacionCombinadaDto plan, bool reemplazar = false)
    {
        var resultado = await Handler().Handle(new EjecutarImportacionCombinadaCommand(plan, reemplazar), CancellationToken.None);
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

    private sealed class AdministracionPermitida : IAutorizacionEscrituraEfectiva
    {
        public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>Ningún plan de estos tests asocia Empresas con Clientes: cualquier llamada sería un error del test.</summary>
    private sealed class RelacionesNoUsadas : IRelacionEmpresarialRepository
    {
        public void Agregar(RelacionEmpresarial relacion) => throw new NotImplementedException();
        public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) => Task.FromResult(ContrapartesVigentes.Vacias);
        public Task<bool> AgregarSiNoVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
