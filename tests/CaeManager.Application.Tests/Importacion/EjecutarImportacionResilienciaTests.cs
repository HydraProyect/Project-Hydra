using CaeManager.Application.Importacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Resiliencia del Importador simple ante datos legítimos que antes lo tumbaban entero (C-06, C-07), conforme al
/// éxito parcial de DCR-12 decisión B: la fila que no puede resolverse queda en <c>Omitidos</c> con su causal propia
/// y el resto del archivo se escribe. Nunca se resuelve un nombre ambiguo al primero que aparezca.
/// </summary>
public class EjecutarImportacionResilienciaTests
{
    private const string CentroHomonimo = "Planta Norte";
    private const string CentroUnico = "Almacén Central";
    private const string RazonSocialRepetida = "ACME Servicios S.L.";
    private const string OtroDni = "00000000T";

    private static AsignacionImportadaDto Asignacion(string dni, string centro) => new(dni, centro, YaExiste: false);

    private static TrabajadorImportadoDto Trabajador(string razonSocial, string dni, string nombre = "Lucía") =>
        new(razonSocial, nombre, "Fernández", dni, null, null, YaExiste: false);

    // ---- C-06: Centros homónimos ----------------------------------------------------------------------------------

    [Fact]
    public async Task Asignacion_a_un_centro_homonimo_se_omite_con_causal_propia_y_no_aborta_el_archivo()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        var plan = EscenarioImportacion.Plan(asignaciones: [Asignacion(EscenarioImportacion.DniConocido, CentroHomonimo)]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("Hay varios centros").And.Contain(CentroHomonimo);
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniConocido);
        resultado.AsignacionesCreadas.Should().Be(0);
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty(
            "asignar al trabajador a uno cualquiera de los homónimos lo pondría en el Centro equivocado");
    }

    [Fact]
    public async Task Centros_homonimos_que_solo_difieren_en_mayusculas_tambien_son_ambiguos()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo.ToUpperInvariant()));
        var plan = EscenarioImportacion.Plan(asignaciones: [Asignacion(EscenarioImportacion.DniConocido, CentroHomonimo)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios centros");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Centros_homonimos_sin_ninguna_asignacion_a_ellos_no_impiden_importar()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        var plan = EscenarioImportacion.Plan(trabajadores:
            [Trabajador(EscenarioImportacion.EmpresaConocida, EscenarioImportacion.DniConocido)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().BeEmpty();
        resultado.TrabajadoresCreados.Should().Be(1, "un dato legítimo (dos Centros homónimos) no puede inutilizar el Importador");
    }

    [Fact]
    public async Task Un_centro_unico_se_asigna_aunque_haya_homonimos_de_otro_nombre()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        escenario.CentrosContexto.ListaCentros.Add(new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroHomonimo));
        var unico = new Centro(Guid.NewGuid(), Guid.NewGuid(), CentroUnico);
        escenario.CentrosContexto.ListaCentros.Add(unico);
        var plan = EscenarioImportacion.Plan(asignaciones:
        [
            Asignacion(EscenarioImportacion.DniConocido, CentroHomonimo),
            Asignacion(EscenarioImportacion.DniConocido, CentroUnico),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.AsignacionesCreadas.Should().Be(1);
        escenario.AsignacionRepositorio.Asignaciones.Should().ContainSingle().Which.CentroId.Should().Be(unico.Id);
        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Hay varios centros");
    }

    // ---- Mayúsculas: razón social y Tipo de documento --------------------------------------------------------------

    [Fact]
    public async Task Razones_sociales_que_solo_difieren_en_mayusculas_omiten_las_filas_que_las_citan_y_el_resto_sigue()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        escenario.EmpresasContexto.ListaEmpresas.Add(new Empresa(RazonSocialRepetida));
        escenario.EmpresasContexto.ListaEmpresas.Add(new Empresa(RazonSocialRepetida.ToUpperInvariant()));
        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto(RazonSocialRepetida, YaExiste: true)],
            trabajadores:
            [
                Trabajador(RazonSocialRepetida, EscenarioImportacion.DniDesconocido),
                Trabajador(EscenarioImportacion.EmpresaConocida, EscenarioImportacion.DniConocido),
            ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().HaveCount(2).And.OnlyContain(o => o.Motivo.Contains("Hay varias empresas"));
        resultado.TrabajadoresCreados.Should().Be(1);
        escenario.TrabajadorRepositorio.Trabajadores.Should().ContainSingle(
            "el trabajador de la razón social ambigua no se cuelga de una Empresa elegida al azar");
    }

    [Fact]
    public async Task Tipos_de_documento_que_solo_difieren_en_mayusculas_omiten_el_documento_y_no_abortan()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente().ConTipoDocumentoExistente();
        escenario.TiposDocumentoContexto.ListaTiposDocumento.Add(
            new TipoDocumento("Formación 20H", 12, true, orden: 2, AmbitoAplicacion.Trabajador));
        escenario.TiposDocumentoContexto.ListaTiposDocumento.Add(
            new TipoDocumento("Reconocimiento médico", 12, true, orden: 3, AmbitoAplicacion.Trabajador));
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 2, 1), YaExiste: false),
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, "Reconocimiento médico", new DateOnly(2026, 2, 1), YaExiste: false),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("varios tipos de documento");
        resultado.DocumentosCreados.Should().Be(1);
        escenario.DocumentoRepositorio.Documentos.Should().ContainSingle();
    }

    // ---- C-07: DNI en la Papelera ---------------------------------------------------------------------------------

    [Fact]
    public async Task Trabajador_nuevo_con_un_DNI_en_la_Papelera_se_omite_con_el_aviso_de_restaurarlo()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);
        var plan = EscenarioImportacion.Plan(trabajadores:
        [
            Trabajador(EscenarioImportacion.EmpresaConocida, EscenarioImportacion.DniConocido),
            Trabajador(EscenarioImportacion.EmpresaConocida, OtroDni, "Marcos"),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Motivo.Should().Contain("Papelera").And.Contain("restáuralo");
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniConocido);
        resultado.TrabajadoresCreados.Should().Be(1, "la fila sana del mismo archivo se escribe: éxito parcial, no 0 de N");
        escenario.TrabajadorRepositorio.Trabajadores.Should().ContainSingle().Which.Dni.Should().Be(OtroDni);
    }

    [Fact]
    public async Task El_DNI_se_compara_normalizado_con_la_Papelera_aunque_el_plan_lo_traiga_en_minusculas_o_con_guiones()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);
        var plan = EscenarioImportacion.Plan(trabajadores:
            [Trabajador(EscenarioImportacion.EmpresaConocida, "1234-5678-z")]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().ContainSingle().Which.Motivo.Should().Contain("Papelera");
        resultado.TrabajadoresCreados.Should().Be(0);
    }

    [Fact]
    public async Task Documento_de_un_DNI_en_la_Papelera_dice_Papelera_y_no_que_no_existe()
    {
        var escenario = new EscenarioImportacion().ConTipoDocumentoExistente();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, new DateOnly(2026, 2, 1), YaExiste: false),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var motivo = resultado.Omitidos.Should().ContainSingle().Subject.Motivo;
        motivo.Should().Contain("Papelera").And.Contain("restáuralo").And.NotContain("no existe");
        escenario.DocumentoRepositorio.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Asignacion_de_un_DNI_en_la_Papelera_dice_Papelera_y_no_que_no_existe()
    {
        var escenario = new EscenarioImportacion().ConCentroExistente();
        escenario.TrabajadorRepositorio.DnisEnPapelera.Add(EscenarioImportacion.DniConocido);
        var plan = EscenarioImportacion.Plan(asignaciones: [Asignacion(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido)]);

        var resultado = await escenario.EjecutarAsync(plan);

        var motivo = resultado.Omitidos.Should().ContainSingle().Subject.Motivo;
        motivo.Should().Contain("Papelera").And.Contain("restáuralo").And.NotContain("no existe");
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_DNI_activo_ni_se_consulta_contra_la_Papelera()
    {
        var escenario = new EscenarioImportacion().ConEmpresaExistente().ConTrabajadorExistente();
        var plan = EscenarioImportacion.Plan(trabajadores:
        [
            new TrabajadorImportadoDto(EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz", EscenarioImportacion.DniConocido, null, null, YaExiste: true),
            Trabajador(EscenarioImportacion.EmpresaConocida, EscenarioImportacion.DniDesconocido),
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().BeEmpty();
        escenario.TrabajadorRepositorio.DnisConsultadosEnPapelera.Should().BeEquivalentTo([EscenarioImportacion.DniDesconocido],
            "el DNI de un Trabajador activo no puede estar además en la Papelera: preguntar por él solo ensancha la consulta");
    }
}
