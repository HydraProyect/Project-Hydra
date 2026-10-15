using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class ObtenerFichaTrabajadorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_detalle_proyecta_la_ficha_completa_para_ambos_vinculos_de_empleador(bool deSubcontrata)
    {
        var empresa = Empresa.CrearComoSubcontrata("Montajes", null, "Supervisado");
        var t = deSubcontrata
            ? Trabajador.DeSubcontrata(empresa.Id, "Paula", "Campos", "12345678Z")
            : Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z");
        t.ActualizarFicha("41 1234567 89", "Electricista", new(1988, 3, 14), "Colombiana", "Plantilla",
            new(2023, 5, 2), new(2026, 10, 15), "Sí, desde mayo de 2023",
            "Residencia y trabajo por cuenta ajena", new(2027, 2, 18), "Se identifica con NIE");
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        var handler = new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso());

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(t.Id); dto.Version.Should().Be(t.Version); dto.EmpleadorNombre.Should().Be("Montajes");
        dto.NumeroAfiliacionSeguridadSocial.Should().Be(t.NumeroAfiliacionSeguridadSocial);
        dto.Puesto.Should().Be(t.Puesto); dto.FechaNacimiento.Should().Be(t.FechaNacimiento);
        dto.Nacionalidad.Should().Be(t.Nacionalidad); dto.TipoVinculo.Should().Be(t.TipoVinculo);
        dto.FechaAltaEmpresa.Should().Be(t.FechaAltaEmpresa); dto.FechaBajaEmpresa.Should().Be(t.FechaBajaEmpresa);
        dto.FiguraEnRnt.Should().Be(t.FiguraEnRnt); dto.TipoPermisoTrabajo.Should().Be(t.TipoPermisoTrabajo);
        dto.CaducidadPermisoTrabajo.Should().Be(t.CaducidadPermisoTrabajo); dto.MotivoPermisoTrabajo.Should().Be(t.MotivoPermisoTrabajo);
    }

    [Fact]
    public async Task El_detalle_invisible_no_devuelve_datos_personales()
    {
        var t = Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z");
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        var handler = new ObtenerTrabajadorPorIdQueryHandler(new EmpresasQueryContextFalso(), trabajadores,
            new AlcanceDatosServiceFalso(trabajadorIdsVisibles: []));
        (await handler.Handle(new(t.Id), CancellationToken.None)).Should().BeNull();
    }
}
