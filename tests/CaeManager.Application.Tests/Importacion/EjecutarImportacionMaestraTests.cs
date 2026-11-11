using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Domain.Centros;
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

        public AltaAcreditacionesPlataformaServiceFalso Altas { get; } = new();
        public CaeManager.Application.Tests.Documentos.SerializacionAltasAcreditacionEmpresaFalsa Serializacion { get; } = new();

        public EjecutarImportacionMaestraCommandHandler Handler()
        {
            // El arnés siembra Empresas y Trabajadores en los contextos de consulta falsos; el handler los lee releídos
            // por los repositorios.
            EmpresaRepositorio.Existentes = () => Empresas.ListaEmpresas;
            TrabajadorRepositorio.Existentes = () => Trabajadores.ListaTrabajadores;
            return new(
                EmpresaRepositorio, Relaciones, CentroRepositorio, TrabajadorRepositorio, Centros, Empresas,
                Operaciones, new Autorizacion(administrador), Altas, Serializacion);
        }

        /// <summary>Como la pantalla: confirma con la huella de una vista previa del mismo plan y modo calculada justo antes.</summary>
        public async Task<Result<ResultadoImportacionMaestraDto>> EjecutarAsync(PlanImportacionMaestraDto plan, bool reemplazar = false)
        {
            var previa = await PrevisualizarAsync(plan, reemplazar);
            return await ConfirmarAsync(plan, reemplazar, previa.EsExitoso ? previa.Valor.CalcularHuella() : string.Empty);
        }

        public async Task<Result<ResultadoImportacionMaestraDto>> ConfirmarAsync(PlanImportacionMaestraDto plan, bool reemplazar, string huella) =>
            await Handler().Handle(new EjecutarImportacionMaestraCommand(plan, reemplazar, huella), CancellationToken.None);

        public async Task<Result<ResultadoImportacionMaestraDto>> PrevisualizarAsync(PlanImportacionMaestraDto plan, bool reemplazar = false) =>
            await Handler().Handle(new PrevisualizarImportacionMaestraCommand(plan, reemplazar), CancellationToken.None);

        private sealed class Autorizacion(bool administrador) : IAutorizacionEscrituraEfectiva
        {
            public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) => Task.FromResult(administrador);

            public Task<AutoridadCargaMasiva> ObtenerAutoridadCargaMasivaAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(administrador ? AutoridadCargaMasiva.Total : AutoridadCargaMasiva.Ninguna);
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
    public async Task Si_otro_rellena_un_campo_entre_la_vista_previa_y_confirmar_en_Reemplazar_no_se_sobrescribe_sin_mostrarlo()
    {
        // Revisión Codex de la PR #2164, r1, MEDIO 1: la vista previa mostró el CNAE vacío (se rellenaría, sin valor previo
        // que avisar); otro Administrador del Tenant lo rellena antes de confirmar. Confirmar no puede sobrescribirlo.
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }]);
        var previa = (await e.PrevisualizarAsync(plan, reemplazar: true)).Valor;
        previa.ValoresSobrescritos.Should().NotContain(v => v.Campo == "CNAE");

        existente.Actualizar(existente.RazonSocial, existente.Cif, "9999", existente.ConvenioAplicable, existente.EsActividadAnexoI);
        var r = await e.ConfirmarAsync(plan, reemplazar: true, previa.CalcularHuella());

        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        existente.Cnae.Should().Be("9999", "el valor que la vista previa no mostró no se sobrescribe");
        existente.ConvenioAplicable.Should().Be("Convenio previo", "no se escribe nada de la fila");
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse("se puede confirmar tras revisar la vista previa");

        // La vista previa nueva sí lo muestra, y con su huella se escribe.
        var revisada = (await e.PrevisualizarAsync(plan, reemplazar: true)).Valor;
        revisada.ValoresSobrescritos.Should().Contain(new ValorPrevioDto("Empresas", 3, "CNAE", "9999", "4321"));
        (await e.ConfirmarAsync(plan, reemplazar: true, revisada.CalcularHuella())).EsExitoso.Should().BeTrue();
        existente.Cnae.Should().Be("4321");
    }

    [Fact]
    public async Task En_Completar_un_cambio_entre_la_vista_previa_y_confirmar_tambien_pide_revisarla()
    {
        // Completar nunca sobrescribe, pero lo escrito tampoco sería lo mostrado (un campo «se rellenará» ya no).
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }]);
        var previa = (await e.PrevisualizarAsync(plan)).Valor;
        previa.CamposRellenados.Should().Be(1);

        existente.Actualizar(existente.RazonSocial, existente.Cif, "9999", existente.ConvenioAplicable, existente.EsActividadAnexoI);
        var r = await e.ConfirmarAsync(plan, reemplazar: false, previa.CalcularHuella());

        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        existente.Cnae.Should().Be("9999");
    }

    [Fact]
    public async Task Cambios_compensados_entre_la_vista_previa_y_confirmar_tambien_piden_revisarla()
    {
        // Revisión Codex de la PR #2164, r2, MEDIO 1: la vista previa iba a rellenar el CNAE de la Empresa A y no el de la
        // B. Otro Administrador del Tenant rellena el de A y vacía el de B antes de confirmar. Las cifras no cambian (una
        // Empresa actualizada, un campo rellenado), pero lo que se escribiría es otro campo de otra Empresa.
        const string cifB = "A28017895";
        var e = new Escenario();
        var a = GestionadaExistente();
        var b = new Empresa("Montajes Ficticios, S.L.", cifB, cnae: "1111", convenioAplicable: "Convenio previo");
        e.Empresas.ListaEmpresas.Add(a);
        e.Empresas.ListaEmpresas.Add(b);
        var plan = Plan([
            FilaEmpresa() with { YaExiste = true },
            new EmpresaMaestraDto(4, "Montajes Ficticios, S.L.", cifB, "4321", "Convenio nuevo", null, YaExiste: true)]);
        var previa = (await e.PrevisualizarAsync(plan)).Valor;
        previa.EmpresasActualizadas.Should().Be(1);
        previa.CamposRellenados.Should().Be(1);
        previa.DetalleCamposRellenados.Should().Equal(new CampoRellenadoDto("Empresas", 3, "CNAE", "4321"));

        a.Actualizar(a.RazonSocial, a.Cif, "9999", a.ConvenioAplicable, a.EsActividadAnexoI);
        b.Actualizar(b.RazonSocial, b.Cif, null, b.ConvenioAplicable, b.EsActividadAnexoI);
        var compensada = (await e.PrevisualizarAsync(plan)).Valor;
        compensada.EmpresasActualizadas.Should().Be(1, "las cifras de la vista previa no cambian");
        compensada.CamposRellenados.Should().Be(1);

        var r = await e.ConfirmarAsync(plan, reemplazar: false, previa.CalcularHuella());

        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        b.Cnae.Should().BeNull("no se rellena un campo que la vista previa no mostró");
        a.Cnae.Should().Be("9999");
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_cambio_entre_la_comprobacion_y_la_escritura_descarta_lo_pendiente_y_no_guarda()
    {
        // La escritura compara su propio resultado antes de guardar: cubre el hueco entre la comprobación y su lectura.
        var e = new Escenario();
        var existente = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(existente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], clientes: [FilaCliente()]);
        var previa = (await e.PrevisualizarAsync(plan, reemplazar: true)).Valor;
        var lecturas = 0;
        // Lecturas: la comprobación (1) y la escritura (2). El cambio llega justo antes de la segunda.
        e.EmpresaRepositorio.CambioEnLaBase = emp =>
        {
            if (emp.Id == existente.Id && ++lecturas == 2)
                emp.Actualizar(emp.RazonSocial, emp.Cif, "9999", emp.ConvenioAplicable, emp.EsActividadAnexoI);
        };

        var r = await e.ConfirmarAsync(plan, reemplazar: true, previa.CalcularHuella());

        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        lecturas.Should().Be(2);
        e.Operaciones.VecesDescartado.Should().BeGreaterThan(0, "lo que la escritura dejó pendiente no llega a ningún guardado posterior");
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse();
    }

    [Fact]
    public void La_huella_distingue_cada_valor_previo_y_cada_motivo_de_omision()
    {
        var baseR = new ResultadoImportacionMaestraDto(0, 1, 0, 0, 0, 0, 0, 0, 0, 1,
            [new ValorPrevioDto("Empresas", 3, "CNAE", "ab", "c")], [], [new ItemImportacionDto("Empresas", 4, "X", "Motivo")]);

        baseR.CalcularHuella().Should().Be((baseR with { }).CalcularHuella());
        baseR.CalcularHuella().Should().NotBe((baseR with { ValoresSobrescritos = [new ValorPrevioDto("Empresas", 3, "CNAE", "a", "bc")] }).CalcularHuella());
        baseR.CalcularHuella().Should().NotBe((baseR with { Omitidos = [new ItemImportacionDto("Empresas", 4, "X", "Otro motivo")] }).CalcularHuella());
        baseR.CalcularHuella().Should().NotBe((baseR with { CamposRellenados = 2 }).CalcularHuella());
        baseR.CalcularHuella().Should().NotBe((baseR with { Omitidos = [], Advertencias = [new ItemImportacionDto("Empresas", 4, "X", "Motivo")] }).CalcularHuella());
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

    [Fact]
    public async Task Un_Centro_existente_en_otra_Empresa_gestionada_no_se_modifica_y_se_informa()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new CaeManager.Domain.Centros.Centro(cliente.Id, Guid.NewGuid(), "Planta Norte", direccion: "Dirección previa");
        e.CentroRepositorio.Centros.Add(centro);

        var r = (await e.EjecutarAsync(
            Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }],
                [FilaCentro() with { YaExiste = true }]), reemplazar: true)).Valor;

        centro.Direccion.Should().Be("Dirección previa");
        r.Omitidos.Should().ContainSingle(o => o.Hoja == "Centros").Which.Motivo.Should().Contain("otra Empresa gestionada");
    }

    [Fact]
    public async Task Si_la_primera_fila_de_un_Cliente_repetido_se_rechaza_la_siguiente_si_lo_actualiza()
    {
        const string cifOtraGestionada = "A28017895";
        var e = new Escenario();
        e.Empresas.ListaEmpresas.Add(GestionadaExistente());
        e.Empresas.ListaEmpresas.Add(new Empresa("Otra Gestionada, S.L.", cifOtraGestionada));
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(cliente);
        var rechazada = new ClienteEmpresarialMaestraDto(3, new string('x', 1000), CifCliente, false, CifGestionada, "Nota A", YaExiste: true);
        var valida = new ClienteEmpresarialMaestraDto(4, "Distribuciones Ficticias, S.A.", CifCliente, false, cifOtraGestionada, "Nota B", YaExiste: true);

        var r = (await e.EjecutarAsync(Plan(clientes: [rechazada, valida]), reemplazar: true)).Valor;

        r.Omitidos.Should().ContainSingle(o => o.Hoja == "Clientes empresariales" && o.Fila == 3);
        cliente.Notas.Should().Be("Nota B", "la primera fila válida del Cliente empresarial es la que se aplica");
        r.ClientesActualizados.Should().Be(1);
        r.CamposRellenados.Should().Be(1, "solo la nota de la fila aplicada; la fila rechazada no deja rellenos");
        r.ValoresSobrescritos.Should().BeEmpty("la razón social de la fila rechazada no se sobrescribió");
    }

    [Fact]
    public async Task El_telefono_normalizado_por_el_dominio_al_mismo_valor_no_cuenta_como_sobrescritura()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(gestionada);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(
            gestionada.Id, "Nombre Ficticio", "Apellido Ejemplo", Dni, new DateOnly(1985, 6, 15), "previo@ejemplo.invalid", telefono: "+34600111222", puesto: "Oficial 1ª");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        var fila = FilaTrabajador(correo: "previo@ejemplo.invalid") with { Telefono = "0034 600 111 222", YaExiste = true };

        var previa = (await e.PrevisualizarAsync(Plan(trabajadores: [fila]), reemplazar: true)).Valor;
        var real = (await e.EjecutarAsync(Plan(trabajadores: [fila]), reemplazar: true)).Valor;

        trabajador.Telefono.Should().Be("+34600111222");
        real.ValoresSobrescritos.Should().NotContain(v => v.Campo == "Teléfono", "el valor persistido no cambió");
        real.TrabajadoresActualizados.Should().Be(0, "ningún valor persistido cambió");
        real.Should().BeEquivalentTo(previa);
    }

    [Fact]
    public async Task Un_cambio_real_de_telefono_informa_el_valor_persistido_no_el_de_la_celda()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(gestionada);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(
            gestionada.Id, "Nombre Ficticio", "Apellido Ejemplo", Dni, new DateOnly(1985, 6, 15), "previo@ejemplo.invalid", telefono: "+34600111222");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        var fila = FilaTrabajador(correo: "previo@ejemplo.invalid") with { Telefono = "0034 611 222 333", YaExiste = true };

        var real = (await e.EjecutarAsync(Plan(trabajadores: [fila]), reemplazar: true)).Valor;

        real.ValoresSobrescritos.Should().ContainSingle(v => v.Campo == "Teléfono")
            .Which.ValorNuevo.Should().Be(trabajador.Telefono).And.Be("+34611222333");
    }

    [Fact]
    public async Task Reemplazar_devuelve_a_gestion_CAE_un_Centro_existente_bajo_el_candado_y_entrega_sus_accesos_de_plataforma_solo_para_Documentos_de_Empresa()
    {
        // Regla A-1: el Centro que vuelve a gestión CAE recibe en sus accesos de plataforma los Documentos de Empresa que
        // exige, igual que EstablecerGestionCaeCentro. El Centro se lee y se cambia bajo el candado.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        e.CentroRepositorio.Centros.Add(centro);
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Plataforma", Guid.NewGuid(), null, null, null);
        e.Centros.ListaCanalesGestionDocumental.Add(acceso);
        e.Centros.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.PorEmail(centro.Id, "Correo CAE", "cae@ejemplo.invalid", null));
        e.Serializacion.AlTomarElCandado = () =>
        {
            centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae, "el Centro se lee y se cambia bajo el candado");
            e.Altas.Recibidas.Should().BeEmpty();
        };

        var r = await e.EjecutarAsync(
            Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }],
                [FilaCentro() with { YaExiste = true }]), reemplazar: true);

        r.EsExitoso.Should().BeTrue();
        centro.GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
        e.Serializacion.Llamadas.Should().Be(1);
        var alta = e.Altas.Recibidas.Should().ContainSingle().Subject;
        alta.Canales.Should().BeEquivalentTo([acceso], "el acceso por correo nunca acredita");
        alta.CentrosQueVuelvenAGestionCae.Should().BeEquivalentTo([centro.Id]);
        alta.SoloDocumentosDeEmpresa.Should().BeTrue("los Documentos de Trabajador no cambian");
    }

    [Fact]
    public async Task Reemplazando_compara_la_vista_previa_con_la_modalidad_releida_bajo_el_candado_y_no_escribe_si_cambio()
    {
        // Revisión Codex de la PR #2104, ronda 1, hallazgo MEDIO: el contexto (uno por circuito) ya rastreaba el Centro
        // con gestión CAE y otra operación lo dejó sin ella; sin relectura, la fila «con gestión CAE» no vería cambio y el
        // Centro se quedaría sin gestión CAE y sin acreditar. Desde la revisión Codex de la PR #2164 (r1, MEDIO 1), la
        // vista previa mostrada no incluía ese cambio de modalidad: la confirmación lo detecta releyendo bajo el candado
        // y no escribe nada.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        e.CentroRepositorio.Centros.Add(centro);
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Plataforma", Guid.NewGuid(), null, null, null);
        e.Centros.ListaCanalesGestionDocumental.Add(acceso);
        e.Serializacion.AlTomarElCandado = () =>
            e.CentroRepositorio.CambioEnLaBase = c => c.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }],
            [FilaCentro() with { YaExiste = true }]);

        var r = await e.EjecutarAsync(plan, reemplazar: true);

        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada,
            "la vista previa no mostró el cambio de modalidad que la base tiene bajo el candado");
        centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae, "no se escribe lo que la vista previa no mostró");
        e.Altas.Recibidas.Should().BeEmpty();
        e.Serializacion.UltimoResultado!.EsFallido.Should().BeTrue("la transacción del candado se deshace");
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse("la operación no se consume: se puede confirmar tras revisar");

        // Revisada de nuevo la vista previa (ya con el cambio), confirmar sí escribe y devuelve el Centro a gestión CAE.
        e.Serializacion.AlTomarElCandado = null;
        var segunda = await e.EjecutarAsync(plan, reemplazar: true);
        segunda.EsExitoso.Should().BeTrue();
        segunda.Valor.ValoresSobrescritos.Should().ContainSingle(v => v.Campo == "Modalidad de gestión CAE");
        centro.GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
        var alta = e.Altas.Recibidas.Should().ContainSingle().Subject;
        alta.Canales.Should().BeEquivalentTo([acceso]);
        alta.CentrosQueVuelvenAGestionCae.Should().BeEquivalentTo([centro.Id]);
    }

    [Fact]
    public async Task Reemplazando_no_casa_una_fila_de_Centro_por_el_CIF_que_la_Empresa_tenia_antes_del_candado()
    {
        // Revisión Codex de la PR #2104, ronda 2, hallazgo MEDIO 1: el contexto (uno por circuito) ya rastreaba la Empresa
        // gestionada con su CIF anterior y otra operación se lo cambió mientras esta esperaba el candado. Sin relectura,
        // la fila de Centro casaría por el CIF viejo, devolvería el Centro a gestión CAE y acreditaría sus Documentos.
        const string cifNuevo = "A28017895";
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        e.CentroRepositorio.Centros.Add(centro);
        e.Centros.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.DePlataforma(centro.Id, "Plataforma", Guid.NewGuid(), null, null, null));
        e.Serializacion.AlTomarElCandado = () => e.EmpresaRepositorio.CambioEnLaBase = emp =>
        {
            if (emp.Id == gestionada.Id && emp.Cif != cifNuevo)
                emp.Actualizar(emp.RazonSocial, cifNuevo, emp.Cnae, emp.ConvenioAplicable, emp.EsActividadAnexoI);
        };

        var plan = Plan(centros: [FilaCentro() with { YaExiste = true }]);

        var r = await e.EjecutarAsync(plan, reemplazar: true);

        // Revisión Codex de la PR #2164, r1, MEDIO 1: la vista previa mostró el Centro devuelto a gestión CAE; releído bajo
        // el candado, se omitiría. No coincide con lo mostrado: no se escribe nada.
        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        e.Serializacion.Llamadas.Should().Be(1);
        centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae);
        e.Altas.Recibidas.Should().BeEmpty("ningún Centro vuelve a gestión CAE");

        var revisada = await e.PrevisualizarAsync(plan, reemplazar: true);
        revisada.Valor.Omitidos.Should().ContainSingle(o => o.Hoja == "Centros", "el CIF de la fila ya no es el de ninguna Empresa gestionada");
    }

    [Fact]
    public async Task Reemplazando_compara_cada_Trabajador_con_lo_releido_bajo_el_candado()
    {
        // Revisión Codex de la PR #2104, ronda 2, hallazgo MEDIO 1 (lectura de Trabajadores): otra operación cambió el
        // correo mientras esta esperaba el candado. Sin relectura, el archivo coincidiría con el valor viejo, no se
        // escribiría nada y el informe no avisaría de la sobrescritura.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        e.CentroRepositorio.Centros.Add(centro);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(
            gestionada.Id, "Nombre Ficticio", "Apellido Ejemplo", Dni, new DateOnly(1985, 6, 15), "previo@ejemplo.invalid",
            puesto: "Oficial 1ª");
        e.Trabajadores.ListaTrabajadores.Add(trabajador);
        e.Serializacion.AlTomarElCandado = () => e.TrabajadorRepositorio.CambioEnLaBase = t =>
        {
            if (t.Email == "previo@ejemplo.invalid")
                t.Actualizar(t.Nombre, t.Apellidos, t.FechaNacimiento, "otro@ejemplo.invalid", t.Observaciones, t.Alias, t.Telefono, t.Puesto);
        };

        var plan = Plan(centros: [FilaCentro() with { YaExiste = true }],
            trabajadores: [FilaTrabajador(correo: "previo@ejemplo.invalid") with { YaExiste = true }]);

        var r = await e.EjecutarAsync(plan, reemplazar: true);

        // Revisión Codex de la PR #2164, r1, MEDIO 1: la vista previa no mostró ninguna sobrescritura del correo; bajo el
        // candado, la habría. No se escribe un valor previo que nadie vio.
        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        e.Serializacion.Llamadas.Should().Be(1);
        trabajador.Email.Should().Be("otro@ejemplo.invalid", "el valor que otro puso en la base no se sobrescribe sin mostrarlo");

        e.Serializacion.AlTomarElCandado = null;
        var segunda = await e.EjecutarAsync(plan, reemplazar: true);
        segunda.EsExitoso.Should().BeTrue();
        trabajador.Email.Should().Be("previo@ejemplo.invalid", "revisada la vista previa, el archivo reemplaza el valor que hay en la base");
        segunda.Valor.TrabajadoresActualizados.Should().Be(1);
        segunda.Valor.ValoresSobrescritos.Should().ContainSingle(v => v.Campo == "Correo")
            .Which.ValorPrevio.Should().Be("otro@ejemplo.invalid");
    }

    [Fact]
    public async Task La_perdedora_de_dos_confirmaciones_con_candado_responde_ya_ejecutada_y_deshace_la_transaccion()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        e.CentroRepositorio.Centros.Add(centro);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }],
            [FilaCentro() with { YaExiste = true }]);
        // La otra confirmación gana mientras esta espera el candado.
        e.Serializacion.AlTomarElCandado = () => e.Operaciones.ConfirmarDesdeOtraEjecucion(plan.OperacionId);

        var r = await e.EjecutarAsync(plan, reemplazar: true);

        r.EsExitoso.Should().BeTrue("para quien llama es «ya ejecutada»");
        r.Valor.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado");
        e.Serializacion.UltimoResultado!.EsFallido.Should().BeTrue("la transacción del candado se deshace");
    }

    [Fact]
    public async Task La_perdedora_que_confirma_entre_la_comprobacion_y_el_guardado_choca_en_el_indice_de_la_operacion()
    {
        // La otra confirmación de la misma operación guarda después de la comprobación bajo el candado (que no la vio)
        // y antes del guardado de esta: la marca de la operación choca en el índice único y no se escribe nada.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        e.Empresas.ListaEmpresas.Add(gestionada);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }]);
        var previa = (await e.PrevisualizarAsync(plan)).Valor;
        var lecturas = 0;
        // Lecturas tras la vista previa: la comprobación (1) y la escritura (2).
        e.EmpresaRepositorio.CambioEnLaBase = emp =>
        {
            if (emp.Id == gestionada.Id && ++lecturas == 2)
                e.Operaciones.ConfirmarDesdeOtraEjecucion(plan.OperacionId);
        };

        var r = await e.ConfirmarAsync(plan, reemplazar: false, previa.CalcularHuella());

        r.EsExitoso.Should().BeTrue("para quien llama es «ya ejecutada»");
        r.Valor.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado");
        e.Serializacion.UltimoResultado!.EsFallido.Should().BeTrue("la transacción del candado se deshace");
    }

    [Fact]
    public async Task En_Completar_un_Centro_que_otra_importacion_crea_mientras_esta_espera_el_candado_no_se_duplica()
    {
        // Revisión Codex de la PR #2164, r2, MEDIO 2: la vista previa mostró el alta del Centro porque no existía. Otra
        // importación maestra del mismo Tenant lo crea y guarda mientras esta espera el candado. No hay índice único por
        // Cliente empresarial y nombre del Centro: solo la relectura BAJO el candado ve el Centro nuevo, y entonces lo que
        // se escribiría ya no es lo mostrado.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }], [FilaCentro()]);
        var previa = (await e.PrevisualizarAsync(plan)).Valor;
        previa.CentrosCreados.Should().Be(1);
        e.Serializacion.AlTomarElCandado = () =>
            e.CentroRepositorio.Centros.Add(new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad"));

        var r = await e.ConfirmarAsync(plan, reemplazar: false, previa.CalcularHuella());

        e.Serializacion.Llamadas.Should().Be(1, "Completar también confirma bajo el candado");
        r.Error.Should().Be(ErroresImportacionMaestra.VistaPreviaCambiada);
        e.CentroRepositorio.Centros.Should().ContainSingle(c => c.Nombre == "Planta Norte", "el Centro no se duplica");
        e.Serializacion.UltimoResultado!.EsFallido.Should().BeTrue("la transacción del candado se deshace");
        (await e.Operaciones.ExisteAsync(plan.OperacionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_reintento_tras_confirmar_bajo_el_candado_responde_ya_ejecutada_y_no_vuelve_a_crear()
    {
        // La estrategia de reintentos repite la operación cuando el primer intento confirmó pero se comunicó como un error
        // transitorio. El reintento relee y vería el Centro que el primero creó: sin comprobar antes la operación bajo el
        // candado, respondería «la vista previa cambió» por una importación que sí se hizo.
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var plan = Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }], [FilaCentro()]);
        e.Serializacion.AntesDeReintentarTrasConfirmar = () => { };

        var r = await e.EjecutarAsync(plan);

        r.EsExitoso.Should().BeTrue();
        r.Valor.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("ya se había confirmado");
        e.CentroRepositorio.Centros.Should().ContainSingle(c => c.Nombre == "Planta Norte");
    }

    [Fact]
    public async Task Completar_vacios_no_cambia_la_modalidad_y_confirma_bajo_el_candado()
    {
        var e = new Escenario();
        var gestionada = GestionadaExistente();
        var cliente = Empresa.CrearComoCliente("Distribuciones Ficticias, S.A.", CifCliente, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        e.Empresas.ListaEmpresas.Add(gestionada);
        e.Empresas.ListaEmpresas.Add(cliente);
        var centro = new Centro(cliente.Id, gestionada.Id, "Planta Norte", direccion: "Calle Ficticia 1, 28000 Ciudad");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        e.CentroRepositorio.Centros.Add(centro);

        var r = await e.EjecutarAsync(
            Plan([FilaEmpresa() with { YaExiste = true }], [FilaCliente() with { YaExiste = true }],
                [FilaCentro() with { YaExiste = true }]));

        r.EsExitoso.Should().BeTrue();
        centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae);
        e.Serializacion.Llamadas.Should().Be(1, "confirmar corre siempre bajo el candado (revisión Codex de la PR #2164, r2, MEDIO 2)");
        e.Altas.Recibidas.Should().BeEmpty();
    }
}