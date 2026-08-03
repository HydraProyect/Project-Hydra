using CaeManager.Application.RequisitosDocumentales.Commands.CrearRequisitoDocumental;
using CaeManager.Application.RequisitosDocumentales.Commands.EditarRequisitoDocumental;
using CaeManager.Application.RequisitosDocumentales.Commands.EliminarRequisitoDocumental;
using CaeManager.Application.RequisitosDocumentales.Commands.MarcarRequisitoDocumentalCumplido;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RequisitosDocumentales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.RequisitosDocumentales;

/// <summary>
/// P1-15 de docs/business/MATURITY_REVIEW.md: RequisitoDocumental tenía
/// Query y pestaña de solo lectura, pero ningún Command — solo se podía
/// insertar una fila a mano en BD. Cierra el CRUD que faltaba.
/// </summary>
public class RequisitoDocumentalCommandsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Requisitos S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa de Requisitos S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con requisitos");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Crear_editar_y_eliminar_un_requisito_documental()
    {
        await using var contexto = CrearContexto();
        var repositorio = new RequisitoDocumentalRepository(contexto);
        var alcanceDatos = new AlcanceDatosServiceFalso();

        var crear = new CrearRequisitoDocumentalCommandHandler(repositorio, alcanceDatos, contexto);
        var resultadoCrear = await crear.Handle(
            new CrearRequisitoDocumentalCommand(_centroId, "Curso de riesgos específicos", "Anual", BloqueaAcceso: true),
            CancellationToken.None);

        resultadoCrear.EsExitoso.Should().BeTrue();
        var id = resultadoCrear.Valor;

        var creado = await contexto.RequisitosDocumentales.SingleAsync(r => r.Id == id);
        creado.Descripcion.Should().Be("Curso de riesgos específicos");
        creado.BloqueaAcceso.Should().BeTrue();
        var versionOriginal = creado.Version;

        var editar = new EditarRequisitoDocumentalCommandHandler(repositorio, alcanceDatos, contexto);
        var resultadoEditar = await editar.Handle(
            new EditarRequisitoDocumentalCommand(id, "Curso de riesgos específicos (actualizado)", "Bianual", false, "Sin notas", versionOriginal),
            CancellationToken.None);

        resultadoEditar.EsExitoso.Should().BeTrue();
        var editado = await contexto.RequisitosDocumentales.SingleAsync(r => r.Id == id);
        editado.Descripcion.Should().Be("Curso de riesgos específicos (actualizado)");
        editado.BloqueaAcceso.Should().BeFalse();
        editado.Version.Should().NotBe(versionOriginal, "ConcurrenciaOptimistaInterceptor renueva la versión en cada modificación");

        var eliminar = new EliminarRequisitoDocumentalCommandHandler(
            repositorio, alcanceDatos, new CurrentUserServiceFalso(usuarioId: Guid.NewGuid()), contexto);
        var resultadoEliminar = await eliminar.Handle(new EliminarRequisitoDocumentalCommand(id), CancellationToken.None);

        resultadoEliminar.EsExitoso.Should().BeTrue();
        (await repositorio.ObtenerPorIdAsync(id)).Should().BeNull("el filtro global oculta los eliminados lógicamente");
    }

    [Fact]
    public async Task Editar_con_una_version_desactualizada_no_pisa_el_cambio_de_otro()
    {
        var alcanceDatos = new AlcanceDatosServiceFalso();
        Guid requisitoId;
        Guid versionVista;

        await using (var contextoInicial = CrearContexto())
        {
            var requisito = new RequisitoDocumental(_centroId, "Requisito original", null, false);
            contextoInicial.RequisitosDocumentales.Add(requisito);
            await contextoInicial.SaveChangesAsync();
            requisitoId = requisito.Id;
            versionVista = requisito.Version;
        }

        // Otra persona lo edita primero, en su propio contexto — cada
        // "usuario" con un contexto propio, igual que en producción (dos
        // pestañas nunca comparten el mismo DbContext).
        await using (var otroContexto = CrearContexto())
        {
            var otroRepositorio = new RequisitoDocumentalRepository(otroContexto);
            var otroHandler = new EditarRequisitoDocumentalCommandHandler(otroRepositorio, alcanceDatos, otroContexto);
            var resultadoOtro = await otroHandler.Handle(
                new EditarRequisitoDocumentalCommand(requisitoId, "Cambiado por otro", null, false, null, versionVista),
                CancellationToken.None);
            resultadoOtro.EsExitoso.Should().BeTrue();
        }

        await using var contexto = CrearContexto();
        var repositorio = new RequisitoDocumentalRepository(contexto);
        var handler = new EditarRequisitoDocumentalCommandHandler(repositorio, alcanceDatos, contexto);
        var resultado = await handler.Handle(
            new EditarRequisitoDocumentalCommand(requisitoId, "Mi cambio, con la versión vieja", null, false, null, versionVista),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
    }

    [Fact]
    public async Task Marcar_un_requisito_como_cumplido_y_luego_reabrirlo()
    {
        await using var contexto = CrearContexto();
        var repositorio = new RequisitoDocumentalRepository(contexto);
        var alcanceDatos = new AlcanceDatosServiceFalso();

        var requisito = new RequisitoDocumental(_centroId, "Formulario de acceso", null, bloqueaAcceso: true);
        contexto.RequisitosDocumentales.Add(requisito);
        await contexto.SaveChangesAsync();

        var marcar = new MarcarRequisitoDocumentalCumplidoCommandHandler(repositorio, alcanceDatos, contexto);
        var resultadoMarcar = await marcar.Handle(new MarcarRequisitoDocumentalCumplidoCommand(requisito.Id, Cumplido: true), CancellationToken.None);

        resultadoMarcar.EsExitoso.Should().BeTrue();
        var cumplido = await contexto.RequisitosDocumentales.SingleAsync(r => r.Id == requisito.Id);
        cumplido.Cumplido.Should().BeTrue();
        cumplido.FechaCumplimiento.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));

        var reabrir = new MarcarRequisitoDocumentalCumplidoCommandHandler(repositorio, alcanceDatos, contexto);
        var resultadoReabrir = await reabrir.Handle(new MarcarRequisitoDocumentalCumplidoCommand(requisito.Id, Cumplido: false), CancellationToken.None);

        resultadoReabrir.EsExitoso.Should().BeTrue();
        var reabierto = await contexto.RequisitosDocumentales.SingleAsync(r => r.Id == requisito.Id);
        reabierto.Cumplido.Should().BeFalse();
        reabierto.FechaCumplimiento.Should().BeNull();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
