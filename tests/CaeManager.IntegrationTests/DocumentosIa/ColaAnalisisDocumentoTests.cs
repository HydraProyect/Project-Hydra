using CaeManager.Application.Common;
using CaeManager.Infrastructure.DocumentosIa;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// La cola que saca del circuito de Blazor la verificación IA y la detección
/// de trabajadores. Lo que importa comprobar es que un encargo encolado se
/// puede leer entero al otro lado —con su tenant y su usuario— porque sin
/// esos dos datos el procesador no puede ni encontrar el documento (el filtro
/// global falla cerrado) ni avisar a nadie.
/// </summary>
public class ColaAnalisisDocumentoTests
{
    private static ColaAnalisisDocumentoEnMemoria CrearCola() =>
        new(NullLogger<ColaAnalisisDocumentoEnMemoria>.Instance);

    [Fact]
    public async Task Un_encargo_encolado_llega_intacto_al_procesador()
    {
        var cola = CrearCola();
        var documentoId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();

        cola.Encolar(new EncargoAnalisisDocumento(documentoId, tenantId, usuarioId, TipoAnalisisDocumento.VerificacionIa));

        var recibido = await cola.Lector.ReadAsync(CancellationToken.None);

        recibido.DocumentoId.Should().Be(documentoId);
        recibido.TenantId.Should().Be(tenantId, "sin tenant el procesador no encuentra el documento");
        recibido.UsuarioSolicitanteId.Should().Be(usuarioId, "sin usuario no hay a quién avisar");
        recibido.Tipo.Should().Be(TipoAnalisisDocumento.VerificacionIa);
    }

    [Fact]
    public async Task Se_conserva_el_orden_de_llegada()
    {
        var cola = CrearCola();
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();

        cola.Encolar(new EncargoAnalisisDocumento(primero, Guid.NewGuid(), null, TipoAnalisisDocumento.DeteccionTrabajadores));
        cola.Encolar(new EncargoAnalisisDocumento(segundo, Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa));

        (await cola.Lector.ReadAsync(CancellationToken.None)).DocumentoId.Should().Be(primero);
        (await cola.Lector.ReadAsync(CancellationToken.None)).DocumentoId.Should().Be(segundo);
    }

    [Fact]
    public async Task Un_mismo_documento_admite_los_dos_analisis()
    {
        // Un documento de ámbito Empresa con detección activa y uno de
        // Trabajador con verificación activa son casos distintos, pero nada
        // impide que la cola transporte ambos tipos a la vez.
        var cola = CrearCola();
        var documentoId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        cola.Encolar(new EncargoAnalisisDocumento(documentoId, tenantId, null, TipoAnalisisDocumento.DeteccionTrabajadores));
        cola.Encolar(new EncargoAnalisisDocumento(documentoId, tenantId, null, TipoAnalisisDocumento.VerificacionIa));

        var tipos = new[]
        {
            (await cola.Lector.ReadAsync(CancellationToken.None)).Tipo,
            (await cola.Lector.ReadAsync(CancellationToken.None)).Tipo
        };

        tipos.Should().BeEquivalentTo(
            [TipoAnalisisDocumento.DeteccionTrabajadores, TipoAnalisisDocumento.VerificacionIa]);
    }

    [Fact]
    public void Encolar_no_bloquea_a_quien_sube_el_documento()
    {
        // El punto entero del cambio: encolar tiene que devolver el control de
        // inmediato, sin esperar a que nadie consuma. Si esto dejara de ser
        // cierto, la subida volvería a esperar al modelo externo.
        var cola = CrearCola();

        var encolar = () => cola.Encolar(
            new EncargoAnalisisDocumento(Guid.NewGuid(), Guid.NewGuid(), null, TipoAnalisisDocumento.VerificacionIa));

        encolar.ExecutionTime().Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }
}
