using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Ámbito efectivo del Coordinador CAE en la carga combinada, que además de crear MODIFICA entidades existentes
/// (Clientes empresariales, Empresas con sus Relaciones Empresariales, Centros, Trabajadores). Cada rama de escritura
/// se prueba con una cartera que deja fuera la entidad: la fila se omite y se cuenta, y no se escribe nada.
/// </summary>
public class EjecutarImportacionCombinadaAmbitoCargaMasivaTests
{
    private const string Proveedora = "Instalaciones Únicas S.L.";
    private const string ClienteDentro = "Cliente Dentro S.A.";
    private const string ClienteFuera = "Cliente Fuera S.A.";

    private static Empresa Cliente(string razonSocial, string cif) =>
        Empresa.CrearComoCliente(razonSocial, cif, false, notas: null, ejecutivoUsuarioId: null);

    private static ClientesFalsos.AlcanceDatosServiceFalso Cartera(
        IReadOnlyList<Guid>? clientes = null, IReadOnlyList<Guid>? empresas = null,
        IReadOnlyList<Guid>? centros = null, IReadOnlyList<Guid>? trabajadores = null) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clientes ?? [], empresaIdsParaGestion: empresas ?? [],
            centroIdsParaGestion: centros ?? [], trabajadorIdsVisibles: trabajadores ?? []);

    private static (EscenarioImportacionCombinada Escenario, Empresa Dentro, Empresa Fuera, Empresa Proveedora) Escenario()
    {
        var escenario = new EscenarioImportacionCombinada();
        var dentro = Cliente(ClienteDentro, "B12345674");
        var fuera = Cliente(ClienteFuera, "A58818501");
        var proveedora = new Empresa(Proveedora);
        escenario.EmpresaRepositorio.Empresas.AddRange([dentro, fuera, proveedora]);
        return (escenario, dentro, fuera, proveedora);
    }

    private static TrabajadorImportadoDto Trabajador() =>
        new(Proveedora, "Lucía", "Fernández", "99999999R", null, null, YaExiste: false, Fila: 2);

    [Fact]
    public async Task Coordinador_no_actualiza_un_Cliente_empresarial_existente_fuera_de_su_ambito()
    {
        var (escenario, dentro, fuera, _) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(clientes:
            [new ClienteImportadoDto("Cliente Fuera Renombrado S.A.", "A58818501", EsCritico: false, YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(clientes: [dentro.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        fuera.RazonSocial.Should().Be(ClienteFuera, "la fila fuera de ámbito no se escribe");
        resultado.ClientesActualizados.Should().Be(0);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Coordinador_con_Reemplazar_deja_intacta_una_Relacion_Empresarial_con_un_Cliente_fuera_de_su_ambito_sin_omitir_la_fila()
    {
        var (escenario, dentro, fuera, proveedora) = Escenario();
        escenario.RelacionesExistentes.Add(RelacionEmpresarial.Crear(proveedora.Id, fuera.Id, DateTime.UtcNow));
        escenario.Relaciones.Permitir = true;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto(Proveedora, [ClienteDentro], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(clientes: [dentro.Id], empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        escenario.Relaciones.Cerradas.Should().BeEmpty("cerrar la Relación con un Cliente ajeno es una escritura fuera de ámbito");
        resultado.Omitidos.Should().BeEmpty("una Relación vigente con un Cliente de fuera no se toca ni omite la fila: omitirla delataría esa Relación");
        resultado.EmpresasActualizadas.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_contador_de_Empresas_actualizadas_no_delata_una_Relacion_Empresarial_con_un_Cliente_fuera_de_ambito(bool conRelacionOculta)
    {
        var (escenario, dentro, fuera, proveedora) = Escenario();
        if (conRelacionOculta)
            escenario.RelacionesExistentes.Add(RelacionEmpresarial.Crear(proveedora.Id, fuera.Id, DateTime.UtcNow));
        escenario.Relaciones.Permitir = true;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto(Proveedora, [], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(clientes: [dentro.Id], empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.EmpresasActualizadas.Should().Be(0, "con o sin Relación oculta el resultado visible es el mismo");
        escenario.Relaciones.Cerradas.Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinador_no_toca_una_Empresa_existente_fuera_de_su_ambito()
    {
        var (escenario, dentro, _, _) = Escenario();
        escenario.Relaciones.Permitir = true;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto(Proveedora, [ClienteDentro], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(clientes: [dentro.Id], empresas: []), autoridad: AutoridadCargaMasiva.Acotada);

        escenario.Relaciones.Agregadas.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Coordinador_no_crea_una_Empresa_asociada_a_un_Cliente_fuera_de_su_ambito()
    {
        var (escenario, dentro, _, _) = Escenario();
        escenario.Relaciones.Permitir = true;
        var antes = escenario.EmpresaRepositorio.Empresas.Count;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto("Empresa Nueva S.L.", [ClienteFuera], YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(clientes: [dentro.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        escenario.EmpresaRepositorio.Empresas.Should().HaveCount(antes);
        resultado.EmpresasCreadas.Should().Be(0);
        escenario.Relaciones.Agregadas.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task D7_un_Cliente_creado_por_el_fichero_no_amplia_el_ambito_y_su_Centro_se_omite()
    {
        var (escenario, _, _, proveedora) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Cliente Nuevo S.A.", "P1234567D", EsCritico: false, YaExiste: false, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Nueva", "Cliente Nuevo S.A.", Proveedora, null, null, null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.ClientesCreados.Should().Be(1, "crear un Cliente empresarial es una alta permitida (D-7: no concede cartera)");
        resultado.CentrosCreados.Should().Be(0);
        escenario.CentroRepositorio.Centros.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    /// <summary>
    /// Decisión de Chris, 2026-11-03: la Empresa que crea la propia carga no entra en el ámbito de quien carga, y el resultado
    /// lo dice en su fila y en las que la citan en vez del motivo uniforme (que aquí no oculta nada).
    /// </summary>
    [Fact]
    public async Task Una_Empresa_creada_por_el_fichero_no_amplia_el_ambito_y_el_resultado_dice_que_pedir()
    {
        const string nueva = "Empresa Nueva S.L.";
        var (escenario, dentro, _, proveedora) = Escenario();
        var alcance = Cartera(clientes: [dentro.Id], empresas: [proveedora.Id]);
        var plan = EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto(nueva, [], YaExiste: false, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Nueva", ClienteDentro, nueva, null, null, null, null, YaExiste: false, Fila: 2)],
            trabajadores: [new TrabajadorImportadoDto(nueva, "Lucía", "Fernández", "99999999R", null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, alcance: alcance, autoridad: AutoridadCargaMasiva.Acotada);

        resultado.EmpresasCreadas.Should().Be(1);
        resultado.CentrosCreados.Should().Be(0);
        resultado.TrabajadoresCreados.Should().Be(0);
        escenario.CentroRepositorio.Centros.Should().BeEmpty();
        escenario.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();

        resultado.Advertencias.Should().ContainSingle().Which.Motivo.Should().Be(
            $"Empresa «{nueva}» creada, pero no queda en tu ámbito durante esta carga: no se da de alta nada que dependa de ella en este archivo. Si al volver a cargar esas filas sigue fuera de tu ámbito, pide a la Dirección CAE que amplíe tu Asignación de Cartera para verla.");
        resultado.Omitidos.Should().HaveCount(2).And.OnlyContain(o => o.Motivo ==
            $"La empresa «{nueva}» se ha creado con esta importación, pero no queda en tu ámbito durante la misma carga: la fila no se ha escrito. Vuelve a cargar esta fila; si sigue fuera de tu ámbito, pide a la Dirección CAE que amplíe tu Asignación de Cartera para verla.");
    }

    [Fact]
    public async Task Una_segunda_fila_de_la_Empresa_creada_no_distingue_un_Cliente_empresarial_inexistente_de_uno_fuera_de_ambito()
    {
        // Codex sobre dff89fbe3: un plan presentado directamente al handler puede repetir la Empresa.
        const string nueva = "Empresa Nueva S.L.";
        async Task<string> MotivoDeLaSegundaFila(string clienteCitado)
        {
            var (escenario, dentro, _, proveedora) = Escenario();
            var plan = EscenarioImportacionCombinada.Plan(empresas:
            [
                new EmpresaCombinadaImportadaDto(nueva, [], YaExiste: false, Fila: 2),
                new EmpresaCombinadaImportadaDto(nueva, [clienteCitado], YaExiste: false, Fila: 3),
            ]);
            var resultado = await escenario.EjecutarAsync(plan,
                alcance: Cartera(clientes: [dentro.Id], empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);
            return resultado.Omitidos.Should().ContainSingle().Which.Motivo;
        }

        (await MotivoDeLaSegundaFila("Cliente Inexistente S.A.")).Should().Be(await MotivoDeLaSegundaFila(ClienteFuera));
    }

    [Fact]
    public async Task Administrador_que_crea_una_Empresa_no_recibe_aviso_de_ambito()
    {
        const string nueva = "Empresa Nueva S.L.";
        var (escenario, _, _, _) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto(nueva, [], YaExiste: false, Fila: 2)],
            trabajadores: [new TrabajadorImportadoDto(nueva, "Lucía", "Fernández", "99999999R", null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(empresas: []), autoridad: AutoridadCargaMasiva.Total);

        resultado.TrabajadoresCreados.Should().Be(1);
        resultado.Advertencias.Should().BeEmpty("la autoridad Total no recorta, así que la Empresa creada queda dentro");
        resultado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_de_una_Empresa_creada_por_el_fichero_y_un_Cliente_fuera_de_ambito_conserva_el_motivo_uniforme()
    {
        const string nueva = "Empresa Nueva S.L.";
        var (escenario, _, _, proveedora) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(
            empresas: [new EmpresaCombinadaImportadaDto(nueva, [], YaExiste: false, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Nueva", ClienteFuera, nueva, null, null, null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(clientes: [], empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Be(
            AmbitoCargaMasiva.Motivo("El Cliente empresarial o la empresa de este centro"),
            "el Cliente empresarial existente fuera del ámbito no se nombra aunque la Empresa la haya creado el fichero");
    }

    [Fact]
    public async Task Coordinador_no_crea_un_Centro_cuya_Empresa_esta_fuera_de_su_ambito()
    {
        var (escenario, dentro, _, _) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(centros:
            [new CentroImportadoDto("Planta Nueva", ClienteDentro, Proveedora, null, null, null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(clientes: [dentro.Id], empresas: []), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.CentrosCreados.Should().Be(0);
        escenario.CentroRepositorio.Centros.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Coordinador_no_da_de_alta_un_Trabajador_de_una_Empresa_fuera_de_su_ambito()
    {
        var (escenario, _, _, _) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]);

        var resultado = await escenario.EjecutarAsync(plan, alcance: Cartera(), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.TrabajadoresCreados.Should().Be(0);
        escenario.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Coordinador_con_la_Empresa_dentro_de_su_ambito_si_da_de_alta_al_Trabajador()
    {
        var (escenario, _, _, proveedora) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]);

        var resultado = await escenario.EjecutarAsync(plan,
            alcance: Cartera(empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.TrabajadoresCreados.Should().Be(1);
        resultado.Omitidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Administrador_no_se_recorta_en_la_carga_combinada()
    {
        var (escenario, _, _, _) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]);

        var resultado = await escenario.EjecutarAsync(plan, alcance: Cartera(), autoridad: AutoridadCargaMasiva.Total);

        resultado.TrabajadoresCreados.Should().Be(1);
    }

    [Fact]
    public async Task Coordinador_no_actualiza_un_Centro_existente_fuera_de_su_ambito()
    {
        var (escenario, dentro, _, proveedora) = Escenario();
        var centro = new Centro(dentro.Id, proveedora.Id, "Planta Vieja", codigoCentro: "OLD");
        escenario.CentrosContexto.ListaCentros.Add(centro);
        var plan = EscenarioImportacionCombinada.Plan(centros:
            [new CentroImportadoDto("Planta Vieja", ClienteDentro, Proveedora, "NUEVO", null, null, null, YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(clientes: [dentro.Id], empresas: [proveedora.Id], centros: []), autoridad: AutoridadCargaMasiva.Acotada);

        centro.CodigoCentro.Should().Be("OLD", "el Centro existente fuera de ámbito no se escribe");
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Coordinador_no_actualiza_un_Trabajador_existente_fuera_de_su_ambito()
    {
        var (escenario, _, _, proveedora) = Escenario();
        var existente = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(proveedora.Id, "Lucía", "Fernández", "99999999R", email: "viejo@x.es");
        escenario.TrabajadoresContexto.ListaTrabajadores.Add(existente);
        var plan = EscenarioImportacionCombinada.Plan(trabajadores:
            [new TrabajadorImportadoDto(Proveedora, "Lucía", "Fernández", "99999999R", null, "nuevo@x.es", YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(empresas: [proveedora.Id], trabajadores: []), autoridad: AutoridadCargaMasiva.Acotada);

        existente.Email.Should().Be("viejo@x.es");
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
    }

    [Fact]
    public async Task Un_DNI_en_la_Papelera_se_omite_con_el_motivo_de_fuera_de_ambito_para_el_Coordinador_y_con_Papelera_para_el_Administrador()
    {
        var (escenario, _, _, proveedora) = Escenario();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add("99999999R");
        var plan = EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]);

        var coordinador = await escenario.EjecutarAsync(plan,
            alcance: Cartera(empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);
        var (escenarioAdmin, _, _, _) = Escenario();
        escenarioAdmin.TrabajadorRepositorio.DnisEnPapelera.Add("99999999R");
        var administrador = await escenarioAdmin.EjecutarAsync(plan, alcance: Cartera(), autoridad: AutoridadCargaMasiva.Total);

        var motivoCoordinador = coordinador.Omitidos.Should().ContainSingle().Subject.Motivo;
        motivoCoordinador.Should().Contain("fuera de tu ámbito").And.NotContain("Papelera",
            "«restáuralo» le diría a un Coordinador CAE que ese DNI existe en el Tenant aunque no sea de su cartera");
        administrador.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Papelera");
    }

    private const string CifDeBaja = "B76543214";

    private static (EscenarioImportacionCombinada Escenario, Empresa Dentro, Empresa DadaDeBaja) EscenarioConBaja()
    {
        var (escenario, dentro, _, _) = Escenario();
        var baja = Cliente("Cliente Dado De Baja S.A.", CifDeBaja);
        baja.MarcarComoEliminado(Guid.NewGuid());
        escenario.EmpresaRepositorio.Empresas.Add(baja);
        return (escenario, dentro, baja);
    }

    [Theory]
    [InlineData("B76543214")]
    [InlineData("  b76543214 ")]
    public async Task Coordinador_con_el_CIF_de_una_Empresa_dada_de_baja_omite_la_fila_sin_abortar_ni_crear_nada(string cif)
    {
        var (escenario, dentro, _) = EscenarioConBaja();
        var antes = escenario.EmpresaRepositorio.Empresas.Count;
        var plan = EscenarioImportacionCombinada.Plan(clientes:
        [
            new ClienteImportadoDto("Nombre Nuevo S.A.", cif, EsCritico: false, YaExiste: false, Fila: 2),
            new ClienteImportadoDto("Otro Nombre S.A.", cif, EsCritico: false, YaExiste: false, Fila: 3),
        ]);

        var resultado = await escenario.Handler(Cartera(clientes: [dentro.Id]), AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(plan, false), default);

        resultado.EsExitoso.Should().BeTrue("el error propio de baja delataría que existe esa Empresa dada de baja");
        resultado.Valor.ClientesCreados.Should().Be(0);
        escenario.EmpresaRepositorio.Empresas.Should().HaveCount(antes, "la fila omitida no escribe nada");
        resultado.Valor.Omitidos.Should().HaveCount(2).And.OnlyContain(o => o.Motivo.Contains("fuera de tu ámbito"));
    }

    [Fact]
    public async Task Las_filas_que_citan_el_Cliente_omitido_por_baja_se_tratan_como_las_de_un_Cliente_fuera_de_ambito()
    {
        var (escenario, dentro, _) = EscenarioConBaja();
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Cliente Dado De Baja S.A.", CifDeBaja, EsCritico: false, YaExiste: false, Fila: 2)],
            empresas: [new EmpresaCombinadaImportadaDto("Proveedora Nueva S.L.", ["Cliente Dado De Baja S.A."], YaExiste: false, Fila: 2)],
            centros: [new CentroImportadoDto("Planta Nueva", "Cliente Dado De Baja S.A.", Proveedora, null, null, null, null, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.Handler(Cartera(clientes: [dentro.Id], empresas: [escenario.EmpresaRepositorio.Empresas.First(e => e.RazonSocial == Proveedora).Id]), AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(plan, false), default);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EmpresasCreadas.Should().Be(0, "la Empresa que cita ese Cliente se omite entera, sin crearla a medias");
        resultado.Valor.CentrosCreados.Should().Be(0);
        resultado.Valor.Omitidos.Should().HaveCount(3).And.OnlyContain(o => o.Motivo.Contains("fuera de tu ámbito"));
    }

    [Fact]
    public async Task Con_todos_los_Clientes_visibles_el_CIF_de_una_baja_conserva_el_error_accionable()
    {
        var (escenario, _, _) = EscenarioConBaja();
        var plan = EscenarioImportacionCombinada.Plan(clientes:
            [new ClienteImportadoDto("Nombre Nuevo S.A.", CifDeBaja, EsCritico: false, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.Handler(new ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: true), AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(plan, false), default);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be("Empresa.CifDeBaja");
    }

    /// <summary>
    /// Asignación de Cartera universal expresada como lista explícita: la lista trae los Clientes empresariales vivos (el
    /// filtro global deja fuera la baja), pero el acto alcanza el Tenant entero (#2117).
    /// </summary>
    private sealed class CarteraUniversalConListaExplicita(IReadOnlyList<Guid> clientes)
        : ClientesFalsos.AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: clientes, empresaIdsParaGestion: [],
            centroIdsParaGestion: [], trabajadorIdsVisibles: []), IAlcanceDatosService
    {
        public Task<bool> AlcanzaElTenantEnteroAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Fact]
    public async Task Con_cartera_universal_en_lista_explicita_el_CIF_de_una_baja_conserva_el_error_accionable()
    {
        var (escenario, dentro, _) = EscenarioConBaja();
        var antes = escenario.EmpresaRepositorio.Empresas.Count;
        var plan = EscenarioImportacionCombinada.Plan(clientes:
            [new ClienteImportadoDto("Nombre Nuevo S.A.", CifDeBaja, EsCritico: false, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.Handler(new CarteraUniversalConListaExplicita([dentro.Id]), AutoridadCargaMasiva.Acotada)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(plan, false), default);

        resultado.EsExitoso.Should().BeFalse("con el Tenant entero alcanzado no hay nada fuera del ámbito que ocultar");
        resultado.Error.Codigo.Should().Be("Empresa.CifDeBaja");
        escenario.EmpresaRepositorio.Empresas.Should().HaveCount(antes, "el error aborta el archivo sin escribir nada");
    }

    [Fact]
    public async Task Administrador_con_el_CIF_de_una_Empresa_dada_de_baja_sigue_viendo_el_error_de_baja()
    {
        var (escenario, _, _) = EscenarioConBaja();
        var plan = EscenarioImportacionCombinada.Plan(clientes:
            [new ClienteImportadoDto("Nombre Nuevo S.A.", CifDeBaja, EsCritico: false, YaExiste: false, Fila: 2)]);

        var resultado = await escenario.Handler(null, AutoridadCargaMasiva.Total)
            .Handle(new Application.Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand(plan, false), default);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be("Empresa.CifDeBaja");
    }

    /// <summary>
    /// RIESGO DECLARADO, aceptado por Chris el 2026-11-02 para la plantilla combinada (y el 2026-11-03 para la genérica, ver
    /// <see cref="EjecutarImportacionAmbitoCargaMasivaTests"/>): al confirmar, lo que existe fuera del ámbito se omite («fuera
    /// de tu ámbito») y lo inexistente se crea, así que el resultado distingue ambos mundos. Es inherente a la clave única (el
    /// alta unitaria falla por duplicado igual). Este test fija el comportamiento medido; si se decide otro diseño, se cambia a
    /// propósito.
    /// </summary>
    [Fact]
    public async Task La_confirmacion_distingue_un_Trabajador_existente_fuera_de_ambito_de_uno_inexistente_riesgo_declarado()
    {
        var (existente, _, _, proveedora1) = Escenario();
        existente.TrabajadorRepositorio.Trabajadores.Add(CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(proveedora1.Id, "Otra", "Persona", "99999999R"));
        existente.TrabajadoresContexto.ListaTrabajadores.AddRange(existente.TrabajadorRepositorio.Trabajadores);
        var (inexistente, _, _, proveedora2) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]);

        var conExistente = await existente.EjecutarAsync(plan, alcance: Cartera(empresas: [proveedora1.Id]), autoridad: AutoridadCargaMasiva.Acotada);
        var sinExistente = await inexistente.EjecutarAsync(plan, alcance: Cartera(empresas: [proveedora2.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        conExistente.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        sinExistente.TrabajadoresCreados.Should().Be(1);
    }

    /// <summary>
    /// Gemelo del anterior para la Empresa del trabajador, mismo RIESGO DECLARADO (Chris, 2026-11-02, reafirmado el
    /// 2026-11-03 para esta rama): la confirmación de la combinada dice «no se encontró la empresa» si no existe y «fuera de tu
    /// ámbito» si existe fuera de él. Fija el comportamiento medido; si se decide otro diseño, se cambia a propósito.
    /// </summary>
    [Fact]
    public async Task La_confirmacion_distingue_la_Empresa_inexistente_de_un_Trabajador_de_una_fuera_de_ambito_riesgo_declarado()
    {
        var (fueraDeAmbito, _, _, _) = Escenario();
        var (inexistente, _, _, _) = Escenario();
        var conEmpresaInexistente = new TrabajadorImportadoDto("Empresa Inexistente S.L.", "Lucía", "Fernández", "99999999R", null, null, YaExiste: false, Fila: 2);

        var conFuera = await fueraDeAmbito.EjecutarAsync(EscenarioImportacionCombinada.Plan(trabajadores: [Trabajador()]),
            alcance: Cartera(empresas: []), autoridad: AutoridadCargaMasiva.Acotada);
        var conInexistente = await inexistente.EjecutarAsync(EscenarioImportacionCombinada.Plan(trabajadores: [conEmpresaInexistente]),
            alcance: Cartera(empresas: []), autoridad: AutoridadCargaMasiva.Acotada);

        conFuera.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
        conInexistente.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("No se encontró la empresa");
        fueraDeAmbito.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();
        inexistente.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_homonima_fuera_de_ambito_que_solo_difiere_en_mayusculas_no_cambia_el_resultado_para_el_Coordinador()
    {
        var (conGemela, _, _, proveedora1) = Escenario();
        conGemela.EmpresaRepositorio.Empresas.Add(new Empresa(Proveedora.ToUpperInvariant()));
        var (sinGemela, _, _, proveedora2) = Escenario();
        var plan = EscenarioImportacionCombinada.Plan(empresas: [new EmpresaCombinadaImportadaDto(Proveedora, [], YaExiste: true, Fila: 2)]);

        var con = await conGemela.EjecutarAsync(plan, alcance: Cartera(empresas: [proveedora1.Id]), autoridad: AutoridadCargaMasiva.Acotada);
        var sin = await sinGemela.EjecutarAsync(plan, alcance: Cartera(empresas: [proveedora2.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        con.Omitidos.Should().BeEmpty("la gemela de fuera del ámbito no puede hacer que el Coordinador vea «varias empresas»");
        con.Should().BeEquivalentTo(sin);
    }

    [Fact]
    public async Task Dos_homonimas_dentro_del_ambito_siguen_siendo_ambiguas()
    {
        var (escenario, _, _, proveedora) = Escenario();
        var gemela = new Empresa(Proveedora.ToUpperInvariant());
        escenario.EmpresaRepositorio.Empresas.Add(gemela);
        var plan = EscenarioImportacionCombinada.Plan(empresas: [new EmpresaCombinadaImportadaDto(Proveedora, [], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, alcance: Cartera(empresas: [proveedora.Id, gemela.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        resultado.Omitidos.Should().ContainSingle("quien ve las dos sí debe saber que son ambiguas");
    }
}
