using CaeManager.Application.Subcontratas.Commands.ActualizarVerificacionExterna;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class ActualizarVerificacionExternaSubcontrataCommandHandlerTests
{
    private static readonly DateOnly Antes = new(2026, 1, 1);

    private static VerificacionExternaSubcontrata CrearVerificacion(Guid subcontrataId) => new(
        subcontrataId, Guid.NewGuid(), Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, Guid.NewGuid(), observaciones: "Original");

    private static (ActualizarVerificacionExternaSubcontrataCommandHandler Handler, UnitOfWorkFalso Uow, FileStorageServiceFalso Archivos)
        Crear(VerificacionExternaSubcontrataRepositorioFalso repositorio, AlcanceDatosServiceFalso? alcance = null)
    {
        var uow = new UnitOfWorkFalso();
        var archivos = new FileStorageServiceFalso();
        return (new ActualizarVerificacionExternaSubcontrataCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), archivos, uow), uow, archivos);
    }

    [Fact]
    public async Task Corrige_los_datos_y_conserva_el_centro_el_tipo_y_la_evidencia()
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        verificacion.AdjuntarEvidencia("ruta-1", "rea.pdf");
        var centro = verificacion.CentroId;
        var tipo = verificacion.TipoDocumentoId;
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, uow, archivos) = Crear(repositorio);

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, new DateOnly(2027, 1, 1), "Corregida"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        verificacion.FechaVerificacion.Should().Be(new DateOnly(2026, 2, 2));
        verificacion.Resultado.Should().Be(ResultadoVerificacionExterna.NoValido);
        verificacion.ValidoHasta.Should().Be(new DateOnly(2027, 1, 1));
        verificacion.Observaciones.Should().Be("Corregida");
        verificacion.CentroId.Should().Be(centro);
        verificacion.TipoDocumentoId.Should().Be(tipo);
        verificacion.EvidenciaArchivoRuta.Should().Be("ruta-1");
        archivos.ArchivosGuardados.Should().Be(0);
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sustituye_la_evidencia_cuando_llega_una_nueva()
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        verificacion.AdjuntarEvidencia("ruta-1", "rea.pdf");
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, _, archivos) = Crear(repositorio);

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, Antes, ResultadoVerificacionExterna.Valido, null, null, [1, 2, 3], "nueva.pdf"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        verificacion.EvidenciaNombreArchivo.Should().Be("nueva.pdf");
        verificacion.EvidenciaArchivoRuta.Should().NotBe("ruta-1");
        archivos.ArchivosGuardados.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_verificacion_no_existe()
    {
        var (handler, uow, _) = Crear(new VerificacionExternaSubcontrataRepositorioFalso());

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.NoEncontrada");
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_como_no_encontrada_cuando_la_subcontrata_no_esta_al_alcance_de_gestion()
    {
        var subcontrataId = Guid.NewGuid();
        var verificacion = CrearVerificacion(subcontrataId);
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, uow, _) = Crear(repositorio,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId], subcontrataIdsParaGestion: []));

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.NoEncontrada");
        verificacion.Observaciones.Should().Be("Original");
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_como_no_encontrada_cuando_el_centro_no_esta_al_alcance_de_gestion()
    {
        var subcontrataId = Guid.NewGuid();
        var verificacion = CrearVerificacion(subcontrataId);
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, uow, _) = Crear(repositorio,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId], centroIdsVisibles: [Guid.NewGuid()]));

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.NoEncontrada");
        verificacion.Resultado.Should().Be(ResultadoVerificacionExterna.Valido);
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_rechaza_una_fecha_futura_y_una_evidencia_sin_nombre()
    {
        var validador = new ActualizarVerificacionExternaSubcontrataCommandValidator();

        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), DiaDeNegocio.Hoy().AddDays(1), ResultadoVerificacionExterna.Valido, null, null)).IsValid.Should().BeFalse();
        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null, [1], null)).IsValid.Should().BeFalse();
        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null, [1], "a.pdf")).IsValid.Should().BeTrue();
    }
}
