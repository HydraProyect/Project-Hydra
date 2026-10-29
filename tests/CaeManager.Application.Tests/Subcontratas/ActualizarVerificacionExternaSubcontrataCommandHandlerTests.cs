using CaeManager.Application.Common;
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
    public async Task Version_antigua_rechaza_la_edicion_sin_guardar_ni_subir_evidencia()
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, uow, archivos) = Crear(repositorio);
        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, Antes, ResultadoVerificacionExterna.NoValido, null, "Cambio",
            "%PDF-1.7\n%%EOF"u8.ToArray(), "nueva.pdf", Guid.NewGuid()), CancellationToken.None);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        verificacion.Observaciones.Should().Be("Original");
        uow.VecesGuardado.Should().Be(0);
        archivos.ArchivosGuardados.Should().Be(0);
    }

    [Theory]
    [InlineData("evidencia.pdf", "MZejecutable")]
    [InlineData("evidencia.pdf", "")]
    [InlineData("evidencia.docx", "PK\u0003\u0004resto")]
    public async Task Rechaza_la_evidencia_invalida_sin_modificar_la_verificacion(string nombre, string contenido)
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        verificacion.AdjuntarEvidencia("ruta-original", "original.pdf");
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var (handler, uow, archivos) = Crear(repositorio);

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, Antes.AddDays(1), ResultadoVerificacionExterna.NoValido, null, "Cambio",
            System.Text.Encoding.Latin1.GetBytes(contenido), nombre, verificacion.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.EvidenciaFormatoNoValido");
        archivos.ArchivosGuardados.Should().Be(0);
        uow.VecesGuardado.Should().Be(0);
        verificacion.FechaVerificacion.Should().Be(Antes);
        verificacion.Resultado.Should().Be(ResultadoVerificacionExterna.Valido);
        verificacion.Observaciones.Should().Be("Original");
        verificacion.EvidenciaArchivoRuta.Should().Be("ruta-original");
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
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, new DateOnly(2027, 1, 1), "Corregida", Version: verificacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(verificacion.Version);
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
    public async Task Devuelve_la_Version_que_queda_tras_guardar_y_no_la_que_llego_en_el_comando()
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var versionVista = verificacion.Version;
        var uow = new UnitOfWorkQueRenuevaLaVersion(verificacion);
        var handler = new ActualizarVerificacionExternaSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new FileStorageServiceFalso(), uow);

        var resultado = await handler.Handle(new ActualizarVerificacionExternaSubcontrataCommand(
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, null, "Corregida", Version: versionVista), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().NotBe(versionVista, "el Deshacer del aviso se manda con este token: el de antes del guardado ya no es el vigente");
        resultado.Valor.Should().Be(verificacion.Version);
    }

    /// <summary>Hace lo que el interceptor de concurrencia al guardar una entidad modificada: renovar su Version.</summary>
    private sealed class UnitOfWorkQueRenuevaLaVersion(VerificacionExternaSubcontrata verificacion) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            typeof(EntidadBase).GetProperty(nameof(EntidadBase.Version))!.SetValue(verificacion, Guid.NewGuid());
            return Task.FromResult(1);
        }
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
            verificacion.Id, Antes, ResultadoVerificacionExterna.Valido, null, null, "%PDF-1.7\n%%EOF"u8.ToArray(), "nueva.pdf", verificacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(verificacion.Version);
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
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, null, null, Version: Guid.NewGuid()), CancellationToken.None);

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
            verificacion.Id, new DateOnly(2026, 2, 2), ResultadoVerificacionExterna.NoValido, null, null, Version: Guid.NewGuid()), CancellationToken.None);

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
            Guid.NewGuid(), DiaDeNegocio.Hoy().AddDays(1), ResultadoVerificacionExterna.Valido, null, null, Version: Guid.NewGuid())).IsValid.Should().BeFalse();
        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null, [1], null, Guid.NewGuid())).IsValid.Should().BeFalse();
        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null, [1], "a.pdf", Guid.NewGuid())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void El_validador_exige_la_Version_vista_en_pantalla()
    {
        var validador = new ActualizarVerificacionExternaSubcontrataCommandValidator();

        validador.Validate(new ActualizarVerificacionExternaSubcontrataCommand(
            Guid.NewGuid(), Antes, ResultadoVerificacionExterna.Valido, null, null)).IsValid.Should().BeFalse();
    }
}
