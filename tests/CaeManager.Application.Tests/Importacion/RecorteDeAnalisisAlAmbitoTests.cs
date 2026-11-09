using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// La vista previa de una carga masiva no debe servir de oráculo de existencia: con autoridad acotada, lo existente fuera
/// del ámbito del Coordinador CAE se presenta como nuevo (<c>YaExiste</c> falso) y la ejecución lo omite y lo cuenta con
/// el motivo uniforme, sin decir que «ya existía». El Administrador no se recorta.
/// </summary>
public class RecorteDeAnalisisAlAmbitoTests
{
    private static PlanImportacionDto Plan() => EscenarioImportacion.Plan(
        empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: true)],
        trabajadores:
        [
            new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: true)
        ],
        documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: true)
        ],
        asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: true)
        ]);

    private static EscenarioImportacion Existente() => new EscenarioImportacion()
        .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente()
        .ConTipoDocumentoExistente().ConDocumentoExistente().ConAsignacionActivaExistente();

    [Fact]
    public async Task Coordinador_no_ve_como_existente_lo_que_esta_fuera_de_su_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [], trabajadorIdsVisibles: [], centroIdsParaGestion: []);

        var plan = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(Plan(), default);

        plan.Empresas.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Trabajadores.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Documentos.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
        plan.Asignaciones.Should().ContainSingle().Which.YaExiste.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinador_sigue_viendo_como_existente_lo_que_esta_dentro_de_su_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            empresaIdsParaGestion: [escenario.EmpresasContexto.ListaEmpresas.Single().Id],
            trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id],
            centroIdsParaGestion: [escenario.CentroExistente!.Id]);

        var plan = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(Plan(), default);

        plan.Empresas.Single().YaExiste.Should().BeTrue();
        plan.Trabajadores.Single().YaExiste.Should().BeTrue();
        plan.Documentos.Single().YaExiste.Should().BeTrue();
        plan.Asignaciones.Single().YaExiste.Should().BeTrue();
    }

    /// <summary>
    /// Plan de la plantilla de Importación con un Trabajador (y su Empresa) dentro del ámbito y una Asignación al Centro
    /// declarado en Centros_Plataformas, tal como lo emite el analizador cuando el Cliente empresarial, el Centro y la
    /// Asignación activa existen (<paramref name="existe"/>) o cuando no existen.
    /// </summary>
    private static PlanImportacionDto PlanConCentroDeAsignacion(bool existe) => EscenarioImportacion.Plan(
        clientesCentros:
        [
            new ClienteCentroImportadoDto(
                EscenarioImportacion.CentroConocido, EsCritico: false, Direccion: null, Contacto: null,
                YaExisteCliente: existe, YaExisteCentro: existe)
        ],
        empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: true)],
        trabajadores:
        [
            new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: true)
        ],
        asignaciones: [new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: existe)]);

    /// <summary>
    /// Mundo con el Cliente empresarial y el Centro (mismo nombre, como los declara Centros_Plataformas) y una Asignación
    /// activa del Trabajador a ese Centro. El Trabajador y su Empresa están siempre dentro del ámbito; el Cliente
    /// empresarial y el Centro, solo si <paramref name="centroVisible"/>.
    /// </summary>
    private static (EscenarioImportacion Escenario, ClientesFalsos.AlcanceDatosServiceFalso Alcance) MundoConCentroDeAsignacion(bool centroVisible)
    {
        var cliente = CaeManager.Domain.Empresas.Empresa.CrearComoCliente(
            EscenarioImportacion.CentroConocido, cif: "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConTrabajadorExistente()
            .ConCentroExistente(clienteId: cliente.Id).ConAsignacionActivaExistente();
        escenario.EmpresasContexto.ListaEmpresas.Add(cliente);
        var empresa = escenario.EmpresasContexto.ListaEmpresas.Single(e => e.RazonSocial == EscenarioImportacion.EmpresaConocida);
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            clienteIdsVisibles: centroVisible ? [cliente.Id] : [],
            empresaIdsParaGestion: [empresa.Id],
            trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id],
            centroIdsParaGestion: centroVisible ? [escenario.CentroExistente!.Id] : []);
        return (escenario, alcance);
    }

    [Fact]
    public async Task Plantilla_de_importacion_no_distingue_el_Centro_de_una_asignacion_fuera_de_ambito_de_uno_inexistente()
    {
        // #1988: el Centro de una Asignación (y su Cliente empresarial y la Asignación activa) existen fuera del ámbito. La
        // vista previa, y con ella los recuentos del título de confirmación, que la página calcula solo con estos YaExiste,
        // deben ser los de un Centro que no existe: «No se creará» para el Cliente empresarial y el Centro, y la Asignación
        // como alta.
        var (escenario, alcance) = MundoConCentroDeAsignacion(centroVisible: false);

        var fuera = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(PlanConCentroDeAsignacion(existe: true), default);

        fuera.Should().BeEquivalentTo(PlanConCentroDeAsignacion(existe: false), o => o.Excluding(p => p.OperacionId).WithStrictOrdering());
    }

    [Fact]
    public async Task Plantilla_de_importacion_conserva_el_Centro_de_una_asignacion_dentro_de_ambito()
    {
        var (escenario, alcance) = MundoConCentroDeAsignacion(centroVisible: true);

        var visible = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(PlanConCentroDeAsignacion(existe: true), default);

        visible.ClientesCentros.Should().ContainSingle().Which.Should().Match<ClienteCentroImportadoDto>(c => c.YaExisteCliente && c.YaExisteCentro);
        visible.Asignaciones.Should().ContainSingle().Which.YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Administrador_no_se_recorta()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);

        var plan = await escenario.Recorte("Administrador", alcance).RecortarAsync(Plan(), default);

        plan.Trabajadores.Single().YaExiste.Should().BeTrue();
        plan.Empresas.Single().YaExiste.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_autoridad_nada_figura_como_existente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true);

        var plan = await escenario.Recorte("GestorCae", alcance).RecortarAsync(Plan(), default);

        plan.Trabajadores.Single().YaExiste.Should().BeFalse("el fallo es cerrado: sin autoridad el ámbito es vacío");
    }

    [Fact]
    public async Task Ejecutar_un_trabajador_existente_fuera_de_ambito_lo_omite_sin_decir_que_ya_existia()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConTrabajadorExistente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [], empresaIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: false)],
            trabajadores:
            [
                new TrabajadorImportadoDto(
                    EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: false)
            ]);

        var resultado = (await escenario.Handler("CoordinadorCae", alcance)
            .Handle(new EjecutarImportacionCommand(plan), default)).Valor;

        resultado.Omitidos.Should().HaveCount(2);
        resultado.Omitidos.Should().OnlyContain(o => o.Motivo.Contains("fuera de tu ámbito"));
        resultado.Omitidos.Should().NotContain(o => o.Motivo.Contains("ya existía"));
    }

    [Fact]
    public async Task Plantilla_de_documentos_trata_un_DNI_fuera_de_ambito_igual_que_uno_inexistente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);
        var plan = EscenarioImportacion.Plan(documentos:
            [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false, Hoja: "Documentos", Fila: 4)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Documentos.Should().BeEmpty();
        var omitido = recortado.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(4);
        omitido.Descripcion.Should().Be(EscenarioImportacion.DniConocido);
        omitido.Motivo.Should().StartWith("No existe ningún trabajador con este DNI.");
    }

    [Fact]
    public async Task Plantilla_de_documentos_conserva_el_DNI_dentro_de_ambito()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id]);
        var plan = EscenarioImportacion.Plan(documentos:
            [new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 1, 1), YaExiste: false, Hoja: "Documentos", Fila: 4)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Documentos.Should().ContainSingle();
        recortado.Omitidos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("No existe este tipo de documento en el catálogo del sistema.")]
    [InlineData("La fecha de emisión (01/01/2099) es futura; no se puede importar.")]
    [InlineData("Fila duplicada dentro del propio archivo.")]
    public async Task Plantilla_de_documentos_no_distingue_por_el_motivo_posterior_un_DNI_fuera_de_ambito(string motivoPosterior)
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);
        var plan = EscenarioImportacion.Plan() with
        {
            Omitidos = [new ItemImportacionDto("Documentos", 5, $"{EscenarioImportacion.DniConocido} — Tipo", motivoPosterior)],
        };

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        var omitido = recortado.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(5);
        omitido.Descripcion.Should().Be(EscenarioImportacion.DniConocido);
        omitido.Motivo.Should().StartWith("No existe ningún trabajador con este DNI.");
    }

    [Theory]
    [InlineData("No existe este tipo de documento en el catálogo del sistema.")]
    [InlineData("La fecha de emisión (01/01/2099) es futura; no se puede importar.")]
    [InlineData("Fila duplicada dentro del propio archivo.")]
    public async Task Plantilla_de_documentos_conserva_el_motivo_posterior_de_un_DNI_dentro_de_ambito(string motivo)
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [escenario.TrabajadorExistente!.Id]);
        var original = new ItemImportacionDto("Documentos", 5, $"{EscenarioImportacion.DniConocido} — Tipo", motivo);
        var plan = EscenarioImportacion.Plan() with { Omitidos = [original] };

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Omitidos.Should().ContainSingle().Which.Should().Be(original);
    }

    [Fact]
    public async Task Plantilla_de_clientes_no_distingue_un_Cliente_fuera_de_ambito_sin_centro_de_uno_inexistente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: [], empresaIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan() with
        {
            Omitidos = [new ItemImportacionDto("Clientes", 3, EscenarioImportacion.EmpresaConocida, "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros.")],
        };

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        var omitido = recortado.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(3);
        omitido.Motivo.Should().StartWith("Este Cliente no existe todavía.");
    }

    [Fact]
    public async Task Plantilla_de_clientes_conserva_el_motivo_del_centro_si_el_Cliente_esta_en_ambito()
    {
        var escenario = Existente();
        var cliente = escenario.EmpresasContexto.ListaEmpresas.Single();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: [], clienteIdsVisibles: [cliente.Id]);
        var original = new ItemImportacionDto("Clientes", 3, EscenarioImportacion.EmpresaConocida, "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros.");
        var plan = EscenarioImportacion.Plan() with { Omitidos = [original] };

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.Omitidos.Should().ContainSingle().Which.Should().Be(original);
    }

    [Fact]
    public async Task Plantilla_de_clientes_trata_un_Cliente_o_Centro_fuera_de_ambito_igual_que_uno_inexistente()
    {
        var escenario = Existente();
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsParaGestion: []);
        var plan = EscenarioImportacion.Plan(clientesCentros:
            [new ClienteCentroImportadoDto(EscenarioImportacion.CentroConocido, false, null, null, YaExisteCliente: true, YaExisteCentro: true, Hoja: "Centros_Plataformas", Fila: 7)]);

        var recortado = await escenario.Recorte("CoordinadorCae", alcance).RecortarAsync(plan, default);

        recortado.ClientesCentros.Should().BeEmpty();
        recortado.Omitidos.Should().ContainSingle().Which.Fila.Should().Be(7);
        recortado.Omitidos.Single().Motivo.Should().Contain("no existe todavía");
    }
}
