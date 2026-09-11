using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.TiposDocumento;

/// <summary>
/// Defecto encontrado al corregir la pantalla Tipos de Documento Gen 2: una fila
/// <see cref="TipoDocumentoCentro"/> tiene índice único por (TenantId, TipoDocumentoId,
/// CentroId). Cuando un Centro ya tiene una exclusión (<c>Incluido=false</c>, dada de alta
/// desde Requisitos del Centro vía <see cref="EstablecerDocumentacionRequeridaCentroCommand"/>)
/// y alguien marca ese mismo Centro en el picker de <c>/tipos-documento</c>,
/// <see cref="EditarTipoDocumentoCommandHandler"/> intentaba crear una SEGUNDA fila para el
/// mismo par — el índice único la rechazaba con un <see cref="DbUpdateException"/> sin
/// capturar (500), y la pantalla no lo sabía ni lo avisaba.
///
/// <para>
/// Contrato fijado tras revisar quién crea las exclusiones (tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
/// § 0.4): <c>TipoDocumentoCentro.Incluido</c> es UNA fila explícita por par, con "la fila
/// explícita manda" sobre el valor general — no dos flujos que compiten por filas separadas.
/// Marcar aquí un Centro ya excluido es una acción tan explícita como la exclusión original:
/// el handler ahora convierte esa fila a <c>Incluido=true</c> en vez de duplicarla, conservando
/// su periodicidad especial, bloqueo de acceso y adjunto de plantilla.
/// </para>
/// </summary>
public class EditarTipoDocumentoConCentroExcluidoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private async Task<(TipoDocumento Tipo, Centro Centro)> CrearTipoYCentroConExclusionAsync(CaeManagerDbContext contexto)
    {
        var cliente = Empresa.CrearComoCliente("Cliente de prueba S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista de prueba S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con exclusión");
        contexto.Centros.Add(centro);

        var tipo = new TipoDocumento(
            "Formación en PRL", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador,
            requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        // La exclusión, dada de alta como lo haría "Requisitos del Centro" (Gestionar en vivo).
        var establecerHandler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepository(contexto), contexto, contexto, contexto);
        var resultadoExclusion = await establecerHandler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(
                centro.Id, tipo.Id, Incluido: false, PeriodicidadEspecialMeses: 6, BloqueaAcceso: true,
                ArchivoUrl: "plantillas/formacion-prl.pdf", NombreArchivoOriginal: "Formación PRL.pdf"),
            CancellationToken.None);
        resultadoExclusion.EsExitoso.Should().BeTrue();

        return (tipo, centro);
    }

    [Fact]
    public async Task Marcar_en_Editar_un_Centro_ya_excluido_no_revienta_el_indice_unico()
    {
        await using var contexto = CrearContexto();
        var (tipo, centro) = await CrearTipoYCentroConExclusionAsync(contexto);

        var editarHandler = new EditarTipoDocumentoCommandHandler(
            new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto, contexto);

        var resultado = await editarHandler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, "Formación en PRL", null, false, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, CentroIds: [centro.Id]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(
            "antes del fix esto lanzaba DbUpdateException por el índice único (TenantId, TipoDocumentoId, CentroId)");
    }

    [Fact]
    public async Task Marcar_en_Editar_un_Centro_ya_excluido_convierte_la_fila_en_vez_de_duplicarla()
    {
        await using var contexto = CrearContexto();
        var (tipo, centro) = await CrearTipoYCentroConExclusionAsync(contexto);

        var editarHandler = new EditarTipoDocumentoCommandHandler(
            new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto, contexto);

        await editarHandler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, "Formación en PRL", null, false, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, CentroIds: [centro.Id]),
            CancellationToken.None);

        await using var lectura = CrearContexto();
        var filas = await lectura.TiposDocumentoCentros
            .Where(tc => tc.TipoDocumentoId == tipo.Id && tc.CentroId == centro.Id)
            .ToListAsync();

        filas.Should().ContainSingle("una sola fila por (Tipo, Centro) — el índice único no permite dos")
            .Which.Should().Match<TipoDocumentoCentro>(f => f.Incluido);

        // La conversión conserva lo que puso "Requisitos del Centro": el picker de
        // Tipos de Documento solo pretende decidir Incluido, no pisar esos campos.
        var fila = filas.Single();
        fila.PeriodicidadEspecialMeses.Should().Be(6);
        fila.BloqueaAcceso.Should().BeTrue();
        fila.ArchivoUrl.Should().Be("plantillas/formacion-prl.pdf");
    }

    [Fact]
    public async Task Dejar_sin_marcar_un_Centro_ya_excluido_no_lo_toca()
    {
        await using var contexto = CrearContexto();
        var (tipo, centro) = await CrearTipoYCentroConExclusionAsync(contexto);

        var editarHandler = new EditarTipoDocumentoCommandHandler(
            new TipoDocumentoRepository(contexto), new TipoDocumentoCentroRepository(contexto), contexto, contexto);

        // Ningún centro marcado en el picker: la exclusión no se ve desde aquí y no debe borrarse.
        var resultado = await editarHandler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, "Formación en PRL", null, false, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, CentroIds: []),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        await using var lectura = CrearContexto();
        var fila = await lectura.TiposDocumentoCentros
            .SingleAsync(tc => tc.TipoDocumentoId == tipo.Id && tc.CentroId == centro.Id);

        fila.Incluido.Should().BeFalse("guardar sin marcar el centro no debe revertir su exclusión");
    }
}
