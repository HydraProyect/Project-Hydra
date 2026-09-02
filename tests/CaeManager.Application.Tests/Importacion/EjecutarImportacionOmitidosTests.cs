using CaeManager.Application.Importacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Contrato «nada se descarta en silencio» (IMPORTACION.md § 3 bis, ratificado
/// por DCR-12 decisión B, propietario 2026-08-24): ninguna fila de un flujo
/// soportado puede desaparecer sin quedar registrada en <c>Omitidos</c> con
/// hoja, descripción y motivo concreto.
///
/// Un test por causal, no una comprobación genérica: la decisión instruye
/// explícitamente no uniformizar las ramas, así que cada test exige el motivo
/// **de su rama** y no se conformaría con un mensaje común. Cada uno comprueba
/// además que no se escribió nada para la fila omitida — contar entradas en
/// <c>Omitidos</c> sin mirar el repositorio no observaría la mitad del contrato.
/// </summary>
public class EjecutarImportacionOmitidosTests
{
    [Fact]
    public async Task Trabajador_cuya_empresa_no_se_resuelve_queda_registrado_nombrando_la_empresa()
    {
        var escenario = new EscenarioImportacion();
        var plan = EscenarioImportacion.Plan(trabajadores:
        [
            new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaDesconocida, "Lucía", "Fernández",
                EscenarioImportacion.DniConocido, null, null, YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Empleados");
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniConocido);
        omitido.Motivo.Should().Contain(EscenarioImportacion.EmpresaDesconocida);
        omitido.Motivo.Should().Contain("no pudo crearse");

        escenario.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();
        resultado.TrabajadoresCreados.Should().Be(0);
    }

    [Fact]
    public async Task Documento_cuyo_trabajador_no_se_resuelve_queda_registrado_nombrando_el_DNI()
    {
        var escenario = new EscenarioImportacion().ConTipoDocumentoExistente();
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniDesconocido, EscenarioImportacion.TipoDocumentoConocido,
                new DateOnly(2026, 2, 1), YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Empleados");
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniDesconocido);
        omitido.Motivo.Should().Contain(EscenarioImportacion.DniDesconocido);
        omitido.Motivo.Should().Contain("no tiene a quién asociarse");

        escenario.DocumentoRepositorio.Documentos.Should().BeEmpty();
        resultado.DocumentosCreados.Should().Be(0);
    }

    [Fact]
    public async Task Documento_con_un_tipo_que_no_existe_en_el_catalogo_queda_registrado_nombrando_el_tipo()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoInexistente,
                new DateOnly(2026, 2, 1), YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Empleados");
        omitido.Descripcion.Should().Contain(EscenarioImportacion.TipoDocumentoInexistente);
        omitido.Motivo.Should().Contain(EscenarioImportacion.TipoDocumentoInexistente);
        omitido.Motivo.Should().Contain("no existe en el catálogo");

        escenario.DocumentoRepositorio.Documentos.Should().BeEmpty();
        resultado.DocumentosCreados.Should().Be(0);
    }

    [Fact]
    public async Task Asignacion_cuyo_trabajador_no_se_resuelve_queda_registrada_nombrando_el_DNI()
    {
        var escenario = new EscenarioImportacion().ConCentroExistente();
        var plan = EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(
                EscenarioImportacion.DniDesconocido, EscenarioImportacion.CentroConocido, YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Asignaciones");
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniDesconocido);
        omitido.Motivo.Should().Contain(EscenarioImportacion.DniDesconocido);

        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
        resultado.AsignacionesCreadas.Should().Be(0);
    }

    /// <summary>
    /// El caso que el E2E congelaba como pérdida silenciosa: el Centro venía en
    /// Centros_Plataformas de este mismo archivo pero no pudo crearse (Fase 10 le
    /// exige una Empresa que la plantilla no recoge). DCR-12 B exige distinguirlo
    /// del Centro que el archivo ni siquiera menciona.
    /// </summary>
    [Fact]
    public async Task Asignacion_cuyo_centro_venia_en_el_archivo_pero_no_pudo_crearse_lo_dice_asi()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        var plan = EscenarioImportacion.Plan(
            clientesCentros: [EscenarioImportacion.CentroDeclaradoEnElArchivo()],
            asignaciones:
            [
                new AsignacionImportadaDto(
                    EscenarioImportacion.DniConocido, EscenarioImportacion.CentroDeclaradoNoCreado, YaExiste: false)
            ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos
            .Should().ContainSingle(o => o.Hoja == "Asignaciones").Subject;
        omitido.Descripcion.Should().Contain(EscenarioImportacion.DniConocido);
        omitido.Descripcion.Should().Contain(EscenarioImportacion.CentroDeclaradoNoCreado);
        omitido.Motivo.Should().Contain(EscenarioImportacion.CentroDeclaradoNoCreado);
        omitido.Motivo.Should().Contain("venía en la hoja Centros_Plataformas");

        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
        resultado.AsignacionesCreadas.Should().Be(0);
    }

    [Fact]
    public async Task Asignacion_cuyo_centro_el_archivo_no_declara_lo_dice_con_otro_motivo()
    {
        var escenario = new EscenarioImportacion().ConTrabajadorExistente();
        var plan = EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.CentroNoDeclarado, YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Asignaciones");
        omitido.Motivo.Should().Contain(EscenarioImportacion.CentroNoDeclarado);
        omitido.Motivo.Should().Contain("no lo declara en la hoja Centros_Plataformas");
        omitido.Motivo.Should().NotContain("venía en la hoja Centros_Plataformas");

        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    /// <summary>
    /// Las cuatro ramas de deduplicación NO son omisiones: el análisis ya anunció
    /// la reutilización marcando la fila <c>YaExiste</c>, y la vista previa no la
    /// contó como "Crear …". Reimportar el mismo archivo debe seguir dando cero
    /// omitidos — si no, una reimportación legítima llenaría la pantalla final de
    /// avisos que el usuario no puede accionar.
    /// </summary>
    [Fact]
    public async Task Reimportar_lo_que_el_analisis_ya_anuncio_como_existente_no_genera_omitidos()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente()
            .ConTrabajadorExistente()
            .ConCentroExistente()
            .ConTipoDocumentoExistente()
            .ConDocumentoExistente()
            .ConAsignacionActivaExistente();

        var plan = EscenarioImportacion.Plan(
            empresas: [new EmpresaImportadaDto(EscenarioImportacion.EmpresaConocida, YaExiste: true)],
            trabajadores:
            [
                new TrabajadorImportadoDto(
                    EscenarioImportacion.EmpresaConocida, "Marta", "Ruiz",
                    EscenarioImportacion.DniConocido, null, null, YaExiste: true)
            ],
            documentos:
            [
                new DocumentoImportadoDto(
                    EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido,
                    new DateOnly(2026, 2, 1), YaExiste: true)
            ],
            asignaciones:
            [
                new AsignacionImportadaDto(
                    EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: true)
            ]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().BeEmpty();
        escenario.EmpresaRepositorio.Empresas.Should().BeEmpty();
        escenario.TrabajadorRepositorio.Trabajadores.Should().BeEmpty();
        escenario.DocumentoRepositorio.Documentos.Should().BeEmpty();
        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    /// <summary>
    /// La otra mitad de la rama de deduplicación: el plan SÍ prometió crear la
    /// Asignación (<c>YaExiste: false</c>) y al confirmar ya existía. Ahí la
    /// pantalla final diría "1 nueva" y confirmaría "0" sin explicación, así que
    /// el contrato exige registrarlo aunque no se escriba nada.
    /// </summary>
    [Fact]
    public async Task Lo_que_el_plan_prometia_crear_y_ya_existe_al_confirmar_queda_registrado()
    {
        var escenario = new EscenarioImportacion()
            .ConTrabajadorExistente()
            .ConCentroExistente()
            .ConAsignacionActivaExistente();

        var plan = EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(
                EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Asignaciones");
        omitido.Motivo.Should().Contain("ya estaba asignado");
        omitido.Motivo.Should().Contain("no lo estaba al analizar");

        escenario.AsignacionRepositorio.Asignaciones.Should().BeEmpty();
        resultado.AsignacionesCreadas.Should().Be(0);
    }
}
