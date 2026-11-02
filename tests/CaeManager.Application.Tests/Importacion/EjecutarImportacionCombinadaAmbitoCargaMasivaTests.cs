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
    public async Task Coordinador_con_Reemplazar_no_cierra_una_Relacion_Empresarial_con_un_Cliente_fuera_de_su_ambito()
    {
        var (escenario, dentro, fuera, proveedora) = Escenario();
        escenario.RelacionesExistentes.Add(RelacionEmpresarial.Crear(proveedora.Id, fuera.Id, DateTime.UtcNow));
        escenario.Relaciones.Permitir = true;
        var plan = EscenarioImportacionCombinada.Plan(empresas:
            [new EmpresaCombinadaImportadaDto(Proveedora, [ClienteDentro], YaExiste: true, Fila: 2)]);

        var resultado = await escenario.EjecutarAsync(plan, reemplazar: true,
            alcance: Cartera(clientes: [dentro.Id], empresas: [proveedora.Id]), autoridad: AutoridadCargaMasiva.Acotada);

        escenario.Relaciones.Cerradas.Should().BeEmpty("cerrar la Relación con un Cliente ajeno es una escritura fuera de ámbito");
        escenario.Relaciones.Agregadas.Should().BeEmpty("la fila se omite entera, no a medias");
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("fuera de tu ámbito");
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
}
