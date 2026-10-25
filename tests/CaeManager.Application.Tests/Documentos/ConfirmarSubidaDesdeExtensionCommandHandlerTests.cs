using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Lo he enviado» de la extensión (incremento E1): la confirmación deja la acreditación Subida con un cambio de estado
/// de origen Extensión, con su autor y su fecha, y ese cambio se puede deshacer desde la extensión.
/// </summary>
public class ConfirmarSubidaDesdeExtensionCommandHandlerTests
{
    private static readonly Guid UsuarioId = Guid.NewGuid();

    private sealed class Escenario
    {
        public required Documento Documento { get; init; }
        public required AcreditacionDocumentoPlataforma Acreditacion { get; init; }
        public required UnitOfWorkFalso UnitOfWork { get; init; }
        public required ConfirmarSubidaDesdeExtensionCommandHandler Confirmar { get; init; }
        public required DeshacerSubidaDesdeExtensionCommandHandler Deshacer { get; init; }
    }

    private static Escenario Crear(
        Action<AcreditacionDocumentoPlataforma>? preparar = null, bool proveedorActivo = true,
        AlcanceDatosServiceFalso? alcance = null)
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify", activo: proveedorActivo);
        var canal = CanalGestionDocumental.DePlataforma(Guid.NewGuid(), "Portal principal", proveedor.Id, null, null, null);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        preparar?.Invoke(acreditacion);

        var documentoRepositorio = new DocumentoRepositorioFalso();
        documentoRepositorio.Agregar(documento);
        var acreditacionRepositorio = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditacionRepositorio.Agregar(acreditacion);
        var centrosContext = new CentrosQueryContextFalso();
        centrosContext.ListaCanalesGestionDocumental.Add(canal);
        var proveedoresContext = new ProveedoresPlataformaCaeQueryContextFalso();
        proveedoresContext.ListaProveedores.Add(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        alcance ??= new AlcanceDatosServiceFalso();

        return new Escenario
        {
            Documento = documento,
            Acreditacion = acreditacion,
            UnitOfWork = unitOfWork,
            Confirmar = new ConfirmarSubidaDesdeExtensionCommandHandler(
                acreditacionRepositorio, documentoRepositorio, alcance, new ProyectosQueryContextFalso(),
                centrosContext, proveedoresContext, new CurrentUserServiceFalso(UsuarioId), unitOfWork),
            Deshacer = new DeshacerSubidaDesdeExtensionCommandHandler(
                acreditacionRepositorio, documentoRepositorio, alcance, new ProyectosQueryContextFalso(), unitOfWork),
        };
    }

    [Fact]
    public async Task Lo_he_enviado_deja_subida_con_un_cambio_de_origen_extension()
    {
        var e = Crear();

        var resultado = await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        var cambio = e.Acreditacion.CambiosEstado.Should().ContainSingle().Subject;
        cambio.Origen.Should().Be(OrigenCambioEstadoAcreditacion.Extension);
        cambio.EstadoAnterior.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        cambio.Estado.Should().Be(EstadoAcreditacion.Subida);
        cambio.UsuarioId.Should().Be(UsuarioId);
        resultado.Valor.CambioId.Should().Be(cambio.Id);
        resultado.Valor.Version.Should().Be(e.Acreditacion.Version);
        e.Acreditacion.UltimoCambioEstadoAManoId.Should().Be(cambio.Id, "la anotación de Centro 360 describe este cambio");
        e.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_rechazada_sin_version_nueva_no_se_confirma()
    {
        var e = Crear(a => a.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow));

        var resultado = await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(MarcarAcreditacionSubidaCommandHandler.CodigoRechazadaSinVersionNueva);
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        e.Acreditacion.CambiosEstado.Should().BeEmpty();
        e.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_conector_inactivo_no_se_confirma()
    {
        var e = Crear(proveedorActivo: false);

        var resultado = await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo);
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        e.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Fuera_de_la_cartera_sale_como_no_encontrada()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]);
        var e = Crear(alcance: alcance);

        var resultado = await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        e.Acreditacion.CambiosEstado.Should().BeEmpty();
        e.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Deshacer_devuelve_el_estado_anterior_y_deja_el_cambio_marcado()
    {
        var e = Crear();
        var confirmado = (await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None)).Valor;

        var resultado = await e.Deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(e.Acreditacion.Id, confirmado.CambioId, confirmado.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        e.Acreditacion.CambiosEstado.Should().ContainSingle().Which.EstaDeshecho.Should().BeTrue("nunca se borra: sigue en el Historial");
        e.UnitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Deshacer_con_una_version_vieja_no_toca_nada()
    {
        var e = Crear();
        var confirmado = (await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None)).Valor;

        var resultado = await e.Deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(e.Acreditacion.Id, confirmado.CambioId, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
    }

    [Fact]
    public async Task Deshacer_fuera_de_la_cartera_sale_como_no_encontrada_y_no_toca_nada()
    {
        var visible = Crear();
        var confirmado = (await visible.Confirmar.Handle(
            new ConfirmarSubidaDesdeExtensionCommand(visible.Acreditacion.Id), CancellationToken.None)).Valor;
        // La misma acreditación, ya confirmada, vista ahora por alguien sin ese Trabajador en su cartera.
        var fueraDeCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]);
        var documentos = new DocumentoRepositorioFalso();
        documentos.Agregar(visible.Documento);
        var acreditaciones = new AcreditacionDocumentoPlataformaRepositorioFalso();
        acreditaciones.Agregar(visible.Acreditacion);
        var unitOfWork = new UnitOfWorkFalso();
        var deshacer = new DeshacerSubidaDesdeExtensionCommandHandler(
            acreditaciones, documentos, fueraDeCartera, new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(visible.Acreditacion.Id, confirmado.CambioId, confirmado.Version),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada", "fuera de cartera no se distingue de no existe");
        visible.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        visible.Acreditacion.CambiosEstado.Should().ContainSingle().Which.EstaDeshecho.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_Centro_360_cambio_el_estado_despues_la_extension_ya_no_lo_deshace()
    {
        var e = Crear();
        var confirmado = (await e.Confirmar.Handle(new ConfirmarSubidaDesdeExtensionCommand(e.Acreditacion.Id), CancellationToken.None)).Valor;
        e.Acreditacion.CambiarEstadoAMano(
            EstadoAcreditacion.Aceptada, new DateOnly(2026, 10, 25), null, null, UsuarioId, DateTime.UtcNow);

        // Con la versión que recibió la extensión: la acreditación cambió después.
        var conVersionVieja = await e.Deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(e.Acreditacion.Id, confirmado.CambioId, confirmado.Version), CancellationToken.None);
        // Aunque llegara con la versión actual, el cambio de la extensión ya no es el que dejó el estado.
        var conVersionActual = await e.Deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(e.Acreditacion.Id, confirmado.CambioId, e.Acreditacion.Version), CancellationToken.None);

        conVersionVieja.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        conVersionActual.Error.Codigo.Should().Be(DeshacerSubidaDesdeExtensionCommandHandler.CodigoYaNoSePuedeDeshacer);
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        e.Acreditacion.CambiosEstado.Should().OnlyContain(c => !c.EstaDeshecho);
        e.UnitOfWork.VecesGuardado.Should().Be(1, "solo guardó la confirmación");
    }

    [Fact]
    public async Task Desde_la_extension_no_se_deshace_un_cambio_de_Centro_360()
    {
        CambioEstadoAcreditacionPlataforma? deCentro360 = null;
        var e = Crear(a => deCentro360 = a.CambiarEstadoAMano(
            EstadoAcreditacion.Subida, new DateOnly(2026, 10, 25), null, null, UsuarioId, DateTime.UtcNow));

        var resultado = await e.Deshacer.Handle(
            new DeshacerSubidaDesdeExtensionCommand(e.Acreditacion.Id, deCentro360!.Id, e.Acreditacion.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        e.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        deCentro360.EstaDeshecho.Should().BeFalse();
    }
}
