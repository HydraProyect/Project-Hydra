using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Tests.Empresas;

public class DuplicadosRazonSocialEmpresaTests
{
    public static TheoryData<string> Comandos => new()
    {
        "CrearCliente", "EditarCliente", "CrearEmpresa", "EditarEmpresa", "CrearSubcontrata", "EditarSubcontrata"
    };

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Razon_social_de_baja_se_rechaza_y_recorta_espacios(string comando)
    {
        var (repo, propia) = Preparar();
        var baja = new Empresa("Identidad reservada");
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        var uow = new UnitOfWorkFalso();
        var resultado = await Ejecutar(comando, repo, propia, uow, " Identidad reservada ");
        resultado.Error.Codigo.Should().Be("Empresa.RazonSocialDeBaja");
        resultado.Error.Mensaje.Should().Contain("Restáurala");
        baja.EstaEliminado.Should().BeTrue();
        uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Baja_con_otras_mayusculas_no_colisiona(string comando)
    {
        // El índice compara el valor exacto: «IDENTIDAD RESERVADA» dada de baja no ocupa «Identidad reservada».
        var (repo, propia) = Preparar();
        var baja = new Empresa("IDENTIDAD RESERVADA");
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        var resultado = await Ejecutar(comando, repo, propia, new UnitOfWorkFalso(), "Identidad reservada");
        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Activa_con_otras_mayusculas_se_sigue_rechazando(string comando)
    {
        // La regla previa de los comandos es más estricta que el índice y se conserva.
        var (repo, propia) = Preparar();
        repo.Agregar(new Empresa("IDENTIDAD RESERVADA"));
        var uow = new UnitOfWorkFalso();
        var resultado = await Ejecutar(comando, repo, propia, uow, "Identidad reservada");
        resultado.Error.Codigo.Should().Be(Prefijo(comando) + ".RazonSocialDuplicada");
        uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Razon_social_activa_duplicada_conserva_el_codigo_del_comando(string comando)
    {
        var (repo, propia) = Preparar();
        repo.Agregar(new Empresa("Identidad reservada"));
        var uow = new UnitOfWorkFalso();
        var resultado = await Ejecutar(comando, repo, propia, uow, " Identidad reservada ");
        resultado.Error.Codigo.Should().Be(Prefijo(comando) + ".RazonSocialDuplicada");
        uow.VecesGuardado.Should().Be(0);
    }

    private static string Prefijo(string comando) =>
        comando.Contains("Subcontrata", StringComparison.Ordinal) ? "Subcontrata"
        : comando.Contains("Cliente", StringComparison.Ordinal) ? "Cliente" : "Empresa";

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Otro_Tenant_propietario_no_cuenta(string comando)
    {
        var (repo, propia) = Preparar();
        var baja = new Empresa("Identidad reservada");
        typeof(EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(baja, Guid.NewGuid());
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        var resultado = await Ejecutar(comando, repo, propia, new UnitOfWorkFalso(), "Identidad reservada");
        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("EditarCliente")]
    [InlineData("EditarEmpresa")]
    [InlineData("EditarSubcontrata")]
    public async Task Su_propia_razon_social_no_es_duplicada(string comando)
    {
        var (repo, propia) = Preparar();
        (await Ejecutar(comando, repo, propia, new UnitOfWorkFalso(), " Identidad original ")).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Carrera_devuelve_el_mismo_error(bool baja)
    {
        var (repo, _) = Preparar();
        var existente = new Empresa("Ganadora");
        if (baja) existente.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(existente);
        var behavior = new DuplicadosEmpresaBehavior<CrearEmpresaCommand, Result<Guid>>(
            new ServiceCollection().AddSingleton<IEmpresaRepository>(repo).BuildServiceProvider());
        var resultado = await behavior.Handle(new CrearEmpresaCommand("Ganadora", null, []),
            _ => throw new DatoEmpresaDuplicadoException(["Ganadora"], true, new InvalidOperationException()), default);
        resultado.Error.Codigo.Should().Be(baja ? "Empresa.RazonSocialDeBaja" : "Empresa.RazonSocialDuplicada");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task Importacion_omite_la_fila_con_la_razon_social_de_una_baja_en_ambas_hojas(bool otroTenant, bool hojaEmpresas, bool reemplazar)
    {
        var repo = new EmpresaRepositorioFalso();
        var baja = new Empresa("Reservada");
        baja.MarcarComoEliminado(Guid.NewGuid());
        if (otroTenant)
            typeof(EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(baja, Guid.NewGuid());
        repo.Agregar(baja);
        var original = Empresa.CrearComoCliente("Original", "B12345674", false, null, null);
        if (reemplazar)
            repo.Agregar(original);
        var antes = repo.Empresas.Count;
        var uow = new UnitOfWorkFalso();
        var resultado = await Importar(repo, uow, hojaEmpresas, reemplazar, " Reservada ");

        // Éxito parcial (DCR-12 B): la fila se omite con su causa y el resto del archivo se guarda.
        resultado.EsExitoso.Should().BeTrue();
        uow.VecesGuardado.Should().Be(1);
        var omitidas = resultado.Valor.Omitidos.Where(o => o.Motivo.Contains("Restáurala", StringComparison.Ordinal));
        if (otroTenant)
        {
            omitidas.Should().BeEmpty();
        }
        else
        {
            omitidas.Should().ContainSingle().Which.Hoja.Should().Be(hojaEmpresas ? "Empresas" : "Clientes");
            repo.Empresas.Count.Should().Be(antes, "la fila omitida no da de alta ninguna Empresa");
            original.RazonSocial.Should().Be("Original", "un renombrado omitido no se aplica");
        }
        baja.EstaEliminado.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Importacion_respeta_mayusculas_del_indice(bool hojaEmpresas)
    {
        var repo = new EmpresaRepositorioFalso();
        var baja = new Empresa("RESERVADA");
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        (await Importar(repo, new UnitOfWorkFalso(), hojaEmpresas, false, "Reservada")).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Fusion_sin_reemplazo_no_comprueba_un_nombre_que_no_se_escribe()
    {
        var repo = new EmpresaRepositorioFalso();
        var baja = new Empresa("Reservada");
        baja.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(baja);
        var existente = Empresa.CrearComoCliente("Original", "B12345674", false, null, null);
        repo.Agregar(existente);
        (await Importar(repo, new UnitOfWorkFalso(), false, false, "Reservada")).EsExitoso.Should().BeTrue();
        existente.RazonSocial.Should().Be("Original");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Importacion_omite_la_segunda_fila_de_Clientes_que_escribe_la_misma_razon_social(bool reemplazar)
    {
        // La consulta solo ve la base: sin el registro en memoria, la segunda fila chocaría con el
        // índice en el SaveChangesAsync final y abortaría el archivo entero.
        var repo = new EmpresaRepositorioConsultaSoloLaBase();
        var original = Empresa.CrearComoCliente("Original", "P1234567D", false, null, null);
        if (reemplazar)
            repo.Agregar(original);
        repo.Guardar();
        var antes = repo.Empresas.Count;
        var uow = new UnitOfWorkFalso();
        var resultado = await Importar(repo, uow, new PlanImportacionCombinadaDto(Guid.NewGuid(),
            [new("Repetida", "B12345674", false, false), new("Repetida", "P1234567D", false, false)], [], [], [], [], []), reemplazar);

        resultado.EsExitoso.Should().BeTrue();
        uow.VecesGuardado.Should().Be(1);
        repo.Empresas.Count(e => e.RazonSocial == "Repetida").Should().Be(1);
        repo.Empresas.Count.Should().Be(antes + 1);
        resultado.Valor.Omitidos.Should().ContainSingle(o => o.Hoja == "Clientes" && o.Descripcion == "Repetida");
        if (reemplazar)
            original.RazonSocial.Should().Be("Original", "el renombrado que repite una razón social de la hoja se omite");
    }

    private static Task<Result<ResultadoImportacionCombinadaDto>> Importar(EmpresaRepositorioFalso repo,
        UnitOfWorkFalso uow, bool hojaEmpresas, bool reemplazar, string razonSocial) =>
        Importar(repo, uow, new PlanImportacionCombinadaDto(Guid.NewGuid(),
            hojaEmpresas ? [] : [new(razonSocial, "B12345674", false, false)],
            hojaEmpresas ? [new(razonSocial, [], false)] : [], [], [], [], []), reemplazar);

    private static Task<Result<ResultadoImportacionCombinadaDto>> Importar(EmpresaRepositorioFalso repo,
        UnitOfWorkFalso uow, PlanImportacionCombinadaDto plan, bool reemplazar)
    {
        var contexto = new CaeManager.Application.Tests.Documentos.EmpresasQueryContextFalso();
        contexto.ListaEmpresas.AddRange(repo.Empresas.Where(e => e.TenantId == repo.TenantPropietarioId && !e.EstaEliminado));
        var handler = new EjecutarImportacionCombinadaCommandHandler(repo, new RelacionesVacias(),
            new CaeManager.Application.Tests.Centros.CentroRepositorioFalso(),
            new CaeManager.Application.Tests.Trabajadores.TrabajadorRepositorioFalso(),
            new CaeManager.Application.Tests.Plantillas.CentrosQueryContextFalso(), contexto,
            new CaeManager.Application.Tests.Plantillas.TrabajadoresQueryContextFalso(),
            new CaeManager.Application.Tests.Importacion.OperacionImportacionSobreUnitOfWorkFalso(uow), new AdministracionPermitida(),
            new CaeManager.Application.Tests.Clientes.AlcanceDatosServiceFalso());
        return handler.Handle(new EjecutarImportacionCombinadaCommand(plan, reemplazar), default);
    }

    /// <summary>Como el repositorio real: la consulta de duplicados no ve las altas pendientes de guardar.</summary>
    private sealed class EmpresaRepositorioConsultaSoloLaBase : EmpresaRepositorioFalso, IEmpresaRepository
    {
        private HashSet<Guid> _guardadas = [];

        /// <summary>Lo agregado hasta aquí cuenta como ya guardado en la base.</summary>
        public void Guardar() => _guardadas = Empresas.Select(e => e.Id).ToHashSet();

        Task<Empresa?> IEmpresaRepository.ObtenerDuplicadaPorRazonSocialAsync(string razonSocial, Guid? excluirId, CancellationToken cancellationToken) =>
            Task.FromResult(Empresas.FirstOrDefault(e => _guardadas.Contains(e.Id) && e.TenantId == TenantPropietarioId
                && e.RazonSocial == razonSocial.Trim() && e.Id != excluirId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Carrera_conserva_el_codigo_de_cada_comando(bool baja)
    {
        var (repo, propia) = Preparar();
        var existente = new Empresa("Ganadora");
        if (baja) existente.MarcarComoEliminado(Guid.NewGuid());
        repo.Agregar(existente);
        var proveedor = new ServiceCollection().AddSingleton<IEmpresaRepository>(repo).BuildServiceProvider();
        var carrera = new DatoEmpresaDuplicadoException(["Ganadora"], true, new InvalidOperationException());

        var crear = await new DuplicadosEmpresaBehavior<CrearClienteCommand, Result<Guid>>(proveedor).Handle(
            new CrearClienteCommand("Ganadora", "B12345674", false, null), _ => throw carrera, default);
        var editar = await new DuplicadosEmpresaBehavior<EditarSubcontrataCommand, Result>(proveedor).Handle(
            new EditarSubcontrataCommand(propia.Id, "Ganadora", "B12345674", [], [], propia.Version), _ => throw carrera, default);

        crear.Error.Codigo.Should().Be(baja ? "Empresa.RazonSocialDeBaja" : "Cliente.RazonSocialDuplicada");
        editar.Error.Codigo.Should().Be(baja ? "Empresa.RazonSocialDeBaja" : "Subcontrata.RazonSocialDuplicada");
    }

    [Theory]
    [InlineData(" Reservada ", false, false)]
    [InlineData("Reservada", true, true)]
    [InlineData("RESERVADA", false, true)]
    public async Task Importacion_simple_comprueba_bajas_normalizacion_y_Tenant_propietario(
        string razonSocial, bool otroTenant, bool exito)
    {
        var escenario = new CaeManager.Application.Tests.Importacion.EscenarioImportacion();
        var baja = new Empresa("Reservada");
        baja.MarcarComoEliminado(Guid.NewGuid());
        if (otroTenant)
            typeof(EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(baja, Guid.NewGuid());
        escenario.EmpresaRepositorio.Agregar(baja);
        var plan = CaeManager.Application.Tests.Importacion.EscenarioImportacion.Plan(
            empresas: [new(razonSocial, false)]);
        var resultado = await escenario.Handler().Handle(
            new CaeManager.Application.Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand(plan), default);
        // Éxito parcial (DCR-12 B): la fila se omite con su causa, no se aborta el archivo.
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EmpresasCreadas.Should().Be(exito ? 1 : 0);
        var omitidas = resultado.Valor.Omitidos.Where(o => o.Motivo.Contains("Restáurala", StringComparison.Ordinal));
        if (exito)
            omitidas.Should().BeEmpty();
        else
            omitidas.Should().ContainSingle().Which.Descripcion.Should().Be(razonSocial);
        baja.EstaEliminado.Should().BeTrue();
    }
    private static (EmpresaRepositorioFalso, Empresa) Preparar()
    {
        var repo = new EmpresaRepositorioFalso();
        var propia = Empresa.CrearComoCliente("Identidad original", "P1234567D", false, null, null);
        repo.Agregar(propia);
        return (repo, propia);
    }

    private static async Task<Result> Ejecutar(string comando, EmpresaRepositorioFalso repo, Empresa empresa,
        UnitOfWorkFalso uow, string razonSocial)
    {
        var contexto = new CaeManager.Application.Tests.Documentos.EmpresasQueryContextFalso();
        var alcance = new AlcanceDatosServiceFalso();
        var relaciones = new RelacionesVacias();
        return comando switch
        {
            "CrearCliente" => await new CrearClienteCommandHandler(repo, uow, new CurrentUserServiceFalso(),
                new TransaccionDeComandoFalsa(), new BloqueoCarteraUsuarioFalso(),
                new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", null, false)))
                .Handle(new CrearClienteCommand(razonSocial, "B12345674", false, null), default),
            "EditarCliente" => await new EditarClienteCommandHandler(repo, alcance, uow)
                .Handle(new EditarClienteCommand(empresa.Id, razonSocial, "B12345674", false, null, empresa.Version), default),
            "CrearEmpresa" => await new CrearEmpresaCommandHandler(repo, relaciones, contexto, alcance, uow)
                .Handle(new CrearEmpresaCommand(razonSocial, "B12345674", []), default),
            "EditarEmpresa" => await new EditarEmpresaCommandHandler(repo, relaciones, contexto, null!, alcance, uow)
                .Handle(new EditarEmpresaCommand(empresa.Id, razonSocial, "B12345674", [], Version: empresa.Version), default),
            "CrearSubcontrata" => await new CrearSubcontrataCommandHandler(repo, relaciones, contexto, alcance, uow)
                .Handle(new CrearSubcontrataCommand(razonSocial, "B12345674", [], []), default),
            "EditarSubcontrata" => await new EditarSubcontrataCommandHandler(repo, relaciones, contexto, null!, alcance, uow)
                .Handle(new EditarSubcontrataCommand(empresa.Id, razonSocial, "B12345674", [], [], empresa.Version), default),
            _ => throw new ArgumentOutOfRangeException(nameof(comando))
        };
    }

    private sealed class AdministracionPermitida : IAutorizacionEscrituraEfectiva
    {
        public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default) => Task.FromResult(AutoridadCargaMasiva.Total);
    }

    private sealed class RelacionesVacias : IRelacionEmpresarialRepository
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
