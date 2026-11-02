using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;
using DocumentosFalsos = CaeManager.Application.Tests.Documentos;
using PlantillasFalsas = CaeManager.Application.Tests.Plantillas;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Paso 1 del Importador (plantilla maestra): modos Completar vacíos / Reemplazar, aviso N14 (nº de campos y valores
/// previos), vista previa sin escribir e idempotencia. Arnés en memoria; la carrera concurrente real vive en IntegrationTests.
/// </summary>
public class EjecutarImportacionMaestraTests
{
    private const string CifGestionada = "B12345674";
    private const string CifCliente = "A58818501";
    private const string Dni = "12345678Z";

    private sealed class Escenario(bool administrador = true)
    {
        public ClientesFalsos.EmpresaRepositorioFalso EmpresaRepositorio { get; } = new();
        public OperacionImportacionRepositorioFalso Operaciones { get; } = new();
        public CaeManager.Application.Tests.Trabajadores.TrabajadorRepositorioFalso TrabajadorRepositorio { get; } = new();
        public CaeManager.Application.Tests.Centros.CentroRepositorioFalso CentroRepositorio { get; } = new();
        public PlantillasFalsas.CentrosQueryContextFalso Centros { get; } = new();
        public DocumentosFalsos.EmpresasQueryContextFalso Empresas { get; } = new();
        public PlantillasFalsas.TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public EscenarioImportacionCombinada.RelacionesRegistradas Relaciones { get; } = new() { Permitir = true };

        public EjecutarImportacionMaestraCommandHandler Handler() => new(
            EmpresaRepositorio, Relaciones, CentroRepositorio, TrabajadorRepositorio, Centros, Empresas, Trabajadores,
            Operaciones, new Autorizacion(administrador));

        public async Task<Result<ResultadoImportacionMaestraDto>> EjecutarAsync(PlanImportacionMaestraDto plan, bool reemplazar = false) =>
            await Handler().Handle(new EjecutarImportacionMaestraCommand(plan, reemplazar), CancellationToken.None);

        public async Task<Result<ResultadoImportacionMaestraDto>> PrevisualizarAsync(PlanImportacionMaestraDto plan, bool reemplazar = false) =>
            await Handler().Handle(new PrevisualizarImportacionMaestraCommand(plan, reemplazar), CancellationToken.None);

        private sealed class Autorizacion(bool administrador) : IAutorizacionEscrituraEfectiva
        {
            public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(administrador);
        }
    }

    private static PlanImportacionMaestraDto Plan(
        IReadOnlyList<EmpresaMaestraDto>? empresas = null, IReadOnlyList<ClienteEmpresarialMaestraDto>? clientes = null,
        IReadOnlyList<CentroMaestraDto>? centros = null, IReadOnlyList<TrabajadorMaestraDto>? trabajadores = null,
        Guid? operacionId = null) =>
        new(operacionId ?? Guid.NewGuid(), empresas ?? [], clientes ?? [], centros ?? [], trabajadores ?? [], [], [], []);

    private static EmpresaMaestraDto FilaEmpresa(string? cnae = "4321", string? convenio = "Convenio nuevo", int fila = 3) =>
        new(fila, "Industrias Ficticias, S.L.", CifGestionada, cnae, convenio, null, YaExiste: false);

    private static CentroMaestraDto FilaCentro(string? contacto = "Persona Ficticia Cinco", int fila = 3) =>
        new(fila, "Planta Norte", CifCliente, CifGestionada, "PN-01", "Calle Ficticia 1, 28000 Ciudad", contacto, null, false, YaExiste: false);

    private static TrabajadorMaestraDto FilaTrabajador(string nombre = "Nombre Ficticio", string? correo = "trabajador@ejemplo.invalid", int fila = 3) =>
        new(fila, nombre, "Apellido Ejemplo", Dni, CifGestionada, "Apellido Ejemplo Nombre", "Oficial 1ª",
            new DateOnly(1985, 6, 15), correo, null, null, YaExiste: false);

    private static ClienteEmpresarialMaestraDto FilaCliente() =>
        new(3, "Distribuciones Ficticias, S.A.", CifCliente, EsCritico: false, CifGestionada, null, YaExiste: false);

    private static Empresa GestionadaExistente(string? convenio = "Convenio previo") =>
        new("Industrias Ficticias, S.L.", CifGestionada, cnae: null, convenioAplicable: convenio);

    [Fact]
    public async Task Un_plan_nuevo_crea_Empresa_Cliente_empresarial_relacion_Centro_y_Trabajador()
    {
        var e = new Escenario();
        var plan = Plan([FilaEmpresa()], [FilaCliente()], [FilaCentro()], [FilaTrabajador()]);

        var r = (await e.EjecutarAsync(plan)).Valor;

        r.EmpresasCreadas.Should().Be(1);
        r.ClientesCreados.Should().Be(1);
        r.RelacionesCreadas.Should().Be(1);
        r.CentrosCreados.Should().Be(1);
        r.TrabajadoresCreados.Should().Be(1);
        r.Omitidos.Should().BeEmpty();
        e.Relaciones.Agregadas.Should().ContainSingle();
        e.CentroRepositorio.Centros.Should().ContainSingle().Which.Direccion.Should().Be("Calle Ficticia 1, 28000 Ciudad");
        e.TrabajadorRepositorio.Trabajadores.Should().ContainSingle().Which.Puesto.Should().Be("Oficial 1ª");
    }

    [Fact]
    public async Task Completar_vacios_rellena_lo_vacio_y_nunca_sobrescribe_un_dato_presente()
    {
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);

        var r = (await e.EjecutarAsync(Plan([FilaEmpresa() with { YaExiste = true }]), reemplazar: false)).Valor;

        r.EmpresasActualizadas.Should().Be(1);
        existente.Cnae.Should().Be("4321", "estaba vacío");
        existente.ConvenioAplicable.Should().Be("Convenio previo", "tenía valor: Completar no lo toca");
        r.CamposRellenados.Should().Be(1);
        r.ValoresSobrescritos.Should().BeEmpty();
    }

    [Fact]
    public async Task Reemplazar_sobrescribe_y_devuelve_la_lista_de_valores_previos_con_hoja_fila_y_campo()
    {
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);

        var r = (await e.EjecutarAsync(Plan([FilaEmpresa(fila: 7) with { YaExiste = true }]), reemplazar: true)).Valor;

        existente.ConvenioAplicable.Should().Be("Convenio nuevo");
        existente.Cnae.Should().Be("4321");
        r.CamposRellenados.Should().Be(1);
        r.ValoresSobrescritos.Should().ContainSingle().Which.Should().Be(
            new ValorPrevioDto("Empresas", 7, "Convenio aplicable", "Convenio previo", "Convenio nuevo"));
    }

    [Fact]
    public async Task Una_celda_vacia_del_archivo_nunca_borra_un_dato_ni_siquiera_en_Reemplazar()
    {
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);

        var r = (await e.EjecutarAsync(Plan([FilaEmpresa(cnae: null, convenio: null) with { YaExiste = true }]), reemplazar: true)).Valor;

        existente.ConvenioAplicable.Should().Be("Convenio previo");
        r.EmpresasActualizadas.Should().Be(0);
        r.ValoresSobrescritos.Should().BeEmpty();
    }

    [Fact]
    public async Task Reemplazar_en_un_Trabajador_existente_lista_el_correo_previo_como_valor_sobrescrito()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(gestionada);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(
            gestionada.Id, "Nombre Ficticio", "Apellido Ejemplo", Dni, new DateOnly(1985, 6, 15), "previo@ejemplo.invalid");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);

        var r = (await e.EjecutarAsync(
            Plan(trabajadores: [FilaTrabajador(correo: "nuevo@ejemplo.invalid", fila: 9) with { YaExiste = true }]), reemplazar: true)).Valor;

        trabajador.Email.Should().Be("nuevo@ejemplo.invalid");
        r.TrabajadoresActualizados.Should().Be(1);
        r.ValoresSobrescritos.Should().Contain(v => v.Hoja == "Trabajadores" && v.Fila == 9 && v.Campo == "Correo" && v.ValorPrevio == "previo@ejemplo.invalid");
    }

    [Fact]
    public async Task La_vista_previa_no_escribe_nada_y_cuenta_lo_mismo_que_confirmar()
    {
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], clientes: [FilaCliente()]);

        var previa = (await e.PrevisualizarAsync(plan, reemplazar: true)).Valor;

        existente.ConvenioAplicable.Should().Be("Convenio previo", "la vista previa no muta nada");
        e.EmpresaRepositorio.Empresas.Should().BeEmpty();
        e.Relaciones.Agregadas.Should().BeEmpty();
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse("la vista previa no consume la operación");

        var real = (await e.EjecutarAsync(plan, reemplazar: true)).Valor;
        real.Should().BeEquivalentTo(previa, "N14: el aviso previo debe coincidir con lo que se hace al confirmar");
    }

    [Fact]
    public async Task Confirmar_dos_veces_la_misma_operacion_no_vuelve_a_escribir()
    {
        var e = new Escenario();
        var plan = Plan([FilaEmpresa()]);

        (await e.EjecutarAsync(plan)).Valor.EmpresasCreadas.Should().Be(1);
        var segunda = (await e.EjecutarAsync(plan)).Valor;

        segunda.EmpresasCreadas.Should().Be(0);
        segunda.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado antes");
        e.EmpresaRepositorio.Empresas.Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_ser_Administrador_ni_confirma_ni_previsualiza()
    {
        var e = new Escenario(administrador: false);
        var plan = Plan([FilaEmpresa()]);

        (await e.EjecutarAsync(plan)).EsExitoso.Should().BeFalse();
        (await e.PrevisualizarAsync(plan)).EsExitoso.Should().BeFalse("la vista previa revela valores previos que pueden ser datos personales");
        e.EmpresaRepositorio.Empresas.Should().BeEmpty();
    }

    private static (Escenario Escenario, CaeManager.Domain.Trabajadores.Trabajador Trabajador) EscenarioConTrabajadorExistente(Guid? empresaId = null)
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(gestionada);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(
            empresaId ?? gestionada.Id, "Nombre Ficticio", "Apellido Ejemplo", Dni, new DateOnly(1985, 6, 15), "previo@ejemplo.invalid");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        return (e, trabajador);
    }

    [Fact]
    public async Task Una_fila_que_el_dominio_rechaza_a_mitad_no_deja_la_entidad_a_medias_ni_pierde_la_sobrescritura_en_el_informe()
    {
        var (e, trabajador) = EscenarioConTrabajadorExistente();
        var puestoDemasiadoLargo = new string('x', 1000);
        var fila = FilaTrabajador(correo: "nuevo@ejemplo.invalid", fila: 9) with { Puesto = puestoDemasiadoLargo, YaExiste = true };

        var previa = (await e.PrevisualizarAsync(Plan(trabajadores: [fila]), reemplazar: true)).Valor;
        var real = (await e.EjecutarAsync(Plan(trabajadores: [fila]), reemplazar: true)).Valor;

        trabajador.Email.Should().Be("previo@ejemplo.invalid", "la fila se omitió: ningún campo suyo puede quedar cambiado");
        real.TrabajadoresActualizados.Should().Be(0);
        real.Omitidos.Should().ContainSingle(o => o.Hoja == "Trabajadores" && o.Fila == 9);
        real.ValoresSobrescritos.Should().BeEmpty();
        real.CamposRellenados.Should().Be(0);
        real.Should().BeEquivalentTo(previa, "la vista previa también valida con el dominio, no solo al escribir");
    }

    [Fact]
    public async Task La_vista_previa_de_una_actualizacion_valida_no_cambia_la_entidad()
    {
        var (e, trabajador) = EscenarioConTrabajadorExistente();

        var previa = (await e.PrevisualizarAsync(
            Plan(trabajadores: [FilaTrabajador(correo: "nuevo@ejemplo.invalid") with { YaExiste = true }]), reemplazar: true)).Valor;

        previa.TrabajadoresActualizados.Should().Be(1);
        trabajador.Email.Should().Be("previo@ejemplo.invalid");
    }

    [Fact]
    public async Task Un_Trabajador_existente_en_otra_Empresa_gestionada_no_se_modifica_y_se_informa()
    {
        var (e, trabajador) = EscenarioConTrabajadorExistente(empresaId: Guid.NewGuid());

        var r = (await e.EjecutarAsync(
            Plan(trabajadores: [FilaTrabajador(correo: "nuevo@ejemplo.invalid") with { YaExiste = true }]), reemplazar: true)).Valor;

        trabajador.Email.Should().Be("previo@ejemplo.invalid");
        r.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("otra Empresa gestionada");
    }
}
