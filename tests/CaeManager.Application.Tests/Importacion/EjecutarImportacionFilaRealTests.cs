using CaeManager.Application.Importacion;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Un omitido detectado al ESCRIBIR (no al analizar) tiene que informar de la fila real del Excel
/// de la que salió, no de la fila 0: el usuario corrige el archivo en esa fila. Un test por hoja o
/// por causal, con una fila distinta en cada elemento del plan para que un número copiado de otro
/// no pase por casualidad.
/// </summary>
public class EjecutarImportacionFilaRealTests
{
    [Fact]
    public async Task Los_omitidos_de_escritura_de_la_plantilla_CAE_completa_llevan_la_fila_de_su_hoja()
    {
        var escenario = new EscenarioImportacion().ConCentroExistente().ConTipoDocumentoExistente();
        var plan = EscenarioImportacion.Plan(
            clientesCentros: [new ClienteCentroImportadoDto(
                "Cliente que no existe", EsCritico: false, Direccion: null, Contacto: null,
                YaExisteCliente: false, YaExisteCentro: false, "Centros_Plataformas", Fila: 11)],
            trabajadores: [new TrabajadorImportadoDto(
                EscenarioImportacion.EmpresaDesconocida, "Lucía", "Fernández", EscenarioImportacion.DniConocido,
                null, null, YaExiste: false, "Empleados", Fila: 23)],
            documentos: [new DocumentoImportadoDto(
                EscenarioImportacion.DniDesconocido, EscenarioImportacion.TipoDocumentoConocido,
                new DateOnly(2026, 2, 1), YaExiste: false, "Empleados", Fila: 37)],
            asignaciones: [new AsignacionImportadaDto(
                EscenarioImportacion.DniDesconocido, EscenarioImportacion.CentroConocido, YaExiste: false, "Asignaciones", Fila: 41)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().Contain(o => o.Hoja == "Centros_Plataformas" && o.Fila == 11);
        resultado.Omitidos.Should().Contain(o => o.Hoja == "Empleados" && o.Descripcion.Contains("Lucía") && o.Fila == 23);
        resultado.Omitidos.Should().Contain(o => o.Hoja == "Empleados" && o.Descripcion.Contains(EscenarioImportacion.TipoDocumentoConocido) && o.Fila == 37);
        resultado.Omitidos.Should().Contain(o => o.Hoja == "Asignaciones" && o.Fila == 41);
        resultado.Omitidos.Should().OnlyContain(o => o.Fila != 0, "ningún omitido de escritura puede quedarse sin su fila");
    }

    [Fact]
    public async Task El_omitido_de_un_documento_de_la_plantilla_de_Documentos_lleva_su_hoja_y_su_fila()
    {
        var escenario = new EscenarioImportacion().ConTipoDocumentoExistente();
        var plan = EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(
                EscenarioImportacion.DniDesconocido, EscenarioImportacion.TipoDocumentoConocido,
                new DateOnly(2026, 2, 1), YaExiste: false, "Documentos", Fila: 9)
        ]);

        var resultado = await escenario.EjecutarAsync(plan);

        var omitido = resultado.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Documentos");
        omitido.Fila.Should().Be(9);
    }

    [Fact]
    public async Task Los_omitidos_de_escritura_de_la_Combinada_llevan_la_fila_de_su_hoja()
    {
        var escenario = new EscenarioImportacionCombinada();
        escenario.EmpresaRepositorio.Agregar(new Empresa("Razón social ya ocupada S.L."));
        var plan = EscenarioImportacionCombinada.Plan(
            clientes: [new ClienteImportadoDto("Razón social ya ocupada S.L.", "B12345674", false, YaExiste: false, Fila: 7)],
            centros: [new CentroImportadoDto(
                "Centro huérfano", "Cliente que no existe", "Empresa que no existe", null, null, null, null, YaExiste: false, Fila: 12)],
            trabajadores: [new TrabajadorImportadoDto(
                "Empresa que no existe", "Lucía", "Fernández", "12345678Z", null, null, YaExiste: false, "Trabajadores", Fila: 20)]);

        var resultado = await escenario.EjecutarAsync(plan);

        resultado.Omitidos.Should().Contain(o => o.Hoja == "Clientes" && o.Fila == 7);
        resultado.Omitidos.Should().Contain(o => o.Hoja == "Centros" && o.Fila == 12);
        resultado.Omitidos.Should().Contain(o => o.Hoja == "Trabajadores" && o.Fila == 20);
        resultado.Omitidos.Should().OnlyContain(o => o.Fila != 0, "ningún omitido de escritura puede quedarse sin su fila");
    }
}
