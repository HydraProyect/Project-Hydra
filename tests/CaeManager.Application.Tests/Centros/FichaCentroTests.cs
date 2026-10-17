using CaeManager.Application.Centros.Commands.EditarFichaCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

public class FichaCentroTests
{
    private static EditarFichaCentroCommand Comando(Centro centro) => new(centro.Id, centro.Version,
        "7 a 19", "Accesos", "100", "Prevención", "200", "prl@example.test", true, false, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_escritura_exige_gestion_aunque_el_Centro_sea_visible_en_lectura(bool gestion)
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        var repositorio = new CentroRepositorioFalso();
        repositorio.Agregar(centro);
        var uow = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(centroIdsParaGestion: gestion ? null : []);
        var resultado = await new EditarFichaCentroCommandHandler(repositorio, alcance, uow).Handle(Comando(centro), CancellationToken.None);
        resultado.EsExitoso.Should().Be(gestion);
        uow.VecesGuardado.Should().Be(gestion ? 1 : 0);
        centro.HorarioAcceso.Should().Be(gestion ? "7 a 19" : null);
        if (!gestion) resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
    }

    [Fact]
    public async Task La_version_antigua_y_el_Centro_ausente_no_guardan()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        var repositorio = new CentroRepositorioFalso();
        var uow = new UnitOfWorkFalso();
        var handler = new EditarFichaCentroCommandHandler(repositorio, new AlcanceDatosServiceFalso(), uow);
        (await handler.Handle(Comando(centro), CancellationToken.None)).Error.Codigo.Should().Be("Centro.NoEncontrado");
        repositorio.Agregar(centro);
        (await handler.Handle(Comando(centro) with { Version = Guid.NewGuid() }, CancellationToken.None))
            .Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        uow.VecesGuardado.Should().Be(0);
        centro.HorarioAcceso.Should().BeNull();
    }

    [Fact]
    public void El_validador_rechaza_correo_longitudes_e_identidad_invalidos()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        var comando = Comando(centro) with
        {
            Id = Guid.Empty,
            Version = Guid.Empty,
            HorarioAcceso = new string('x', 501),
            ControlAccesosNombre = new string('x', 201),
            ControlAccesosTelefono = new string('x', 51),
            TecnicoPrevencionNombre = new string('x', 201),
            TecnicoPrevencionTelefono = new string('x', 51),
            TecnicoPrevencionCorreo = "correo-invalido"
        };
        new EditarFichaCentroCommandValidator().Validate(comando).Errors.Select(e => e.PropertyName).Should()
            .BeEquivalentTo(new[] { nameof(comando.Id), nameof(comando.Version), nameof(comando.HorarioAcceso),
                nameof(comando.ControlAccesosNombre), nameof(comando.ControlAccesosTelefono), nameof(comando.TecnicoPrevencionNombre),
                nameof(comando.TecnicoPrevencionTelefono), nameof(comando.TecnicoPrevencionCorreo) });
        new EditarFichaCentroCommandValidator().Validate(Comando(centro) with { TecnicoPrevencionCorreo = " " }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae", true, true)]
    [InlineData("Consulta", true, true)]
    [InlineData("GestorCae", false, false)]
    [InlineData("Cliente", true, false)]
    [InlineData(null, true, false)]
    public async Task La_lectura_no_expone_la_ficha_fuera_de_su_politica_y_ambito(string? rol, bool gestion, bool visible)
    {
        var titular = new Empresa("Empresa contraparte de prueba");
        var ejecutora = new Empresa("Empresa ejecutora de prueba");
        var centro = new Centro(titular.Id, ejecutora.Id, "Centro de prueba");
        centro.ActualizarFicha("7 a 19", "Accesos", "100", "Prevención", "200", "prl@example.test", true, false, null);
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([titular, ejecutora]);
        var alcance = new AlcanceDatosServiceFalso(centroIdsParaGestion: gestion ? null : []);
        var handler = new ObtenerCentroPorIdQueryHandler(centros, empresas, alcance, PoliticaNotaInternaPruebas.Con(rol));
        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);
        dto.Should().NotBeNull();
        dto!.FichaVisible.Should().Be(visible);
        dto.HorarioAcceso.Should().Be(visible ? "7 a 19" : null);
        dto.ControlAccesosNombre.Should().Be(visible ? "Accesos" : null);
        dto.ControlAccesosTelefono.Should().Be(visible ? "100" : null);
        dto.TecnicoPrevencionNombre.Should().Be(visible ? "Prevención" : null);
        dto.TecnicoPrevencionTelefono.Should().Be(visible ? "200" : null);
        dto.TecnicoPrevencionCorreo.Should().Be(visible ? "prl@example.test" : null);
        dto.TrabajosEnAltura.Should().Be(visible ? true : null);
        dto.EspaciosConfinados.Should().Be(visible ? false : null);
        dto.AtmosferasExplosivas.Should().BeNull();
        var sinLectura = new ObtenerCentroPorIdQueryHandler(centros, empresas,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: []), PoliticaNotaInternaPruebas.Con(rol));
        (await sinLectura.Handle(new(centro.Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task La_impersonacion_no_recibe_los_contactos_de_la_ficha()
    {
        var titular = new Empresa("Empresa contraparte de prueba");
        var centro = new Centro(titular.Id, titular.Id, "Centro de prueba");
        centro.ActualizarFicha("7 a 19", null, null, "Prevención", null, "prl@example.test", null, null, null);
        var centros = new CentrosQueryContextFalso(); centros.ListaCentros.Add(centro);
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(titular);
        var handler = new ObtenerCentroPorIdQueryHandler(centros, empresas, new AlcanceDatosServiceFalso(),
            PoliticaNotaInternaPruebas.Con("GestorCae", PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));
        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);
        dto!.FichaVisible.Should().BeFalse();
        dto.TecnicoPrevencionCorreo.Should().BeNull();
    }
}
