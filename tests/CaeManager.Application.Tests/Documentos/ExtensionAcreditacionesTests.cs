using System.Text.Json;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ExtensionAcreditacionesTests
{
    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    public async Task Filtra_solo_el_rechazo_vigente_en_la_extension(bool renovada, bool extension, int esperadas)
    {
        var documentos = new DocumentosQueryContextFalso();
        var centros = new CentrosQueryContextFalso();
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var tipos = new TiposDocumentoQueryContextFalso();
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de Trabajo");
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Certificado", null, false, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        var documento = Documento.DeCliente(Guid.NewGuid(), tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Ilegible", DateTime.UtcNow);
        documentos.ListaDocumentos.Add(documento);
        documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        documentos.ListaRechazosAcreditacionDocumentoPlataforma.AddRange(acreditacion.HistorialRechazos);
        if (renovada)
        {
            var nuevo = documento.NuevoDelMismoTitular(new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca, "corregido.pdf", null);
            documento.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
            documentos.ListaDocumentos.Add(nuevo);
            documentos.ListaAcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(nuevo.Id, canal.Id));
        }
        centros.ListaCentros.Add(centro);
        centros.ListaCanalesGestionDocumental.Add(canal);
        proveedores.ListaProveedores.Add(proveedor);
        tipos.ListaTiposDocumento.Add(tipo);
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(documentos, centros, proveedores,
            new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), tipos, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: extension), CancellationToken.None);

        resultado.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).Should().HaveCount(esperadas);
    }

    [Fact]
    public async Task Cada_proveedor_lleva_sus_dominios_y_no_los_de_otro()
    {
        var documentos = new DocumentosQueryContextFalso();
        var centros = new CentrosQueryContextFalso();
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var tipos = new TiposDocumentoQueryContextFalso();
        var nalanda = new ProveedorPlataformaCae("nalanda", "Nalanda");
        var dokify = new ProveedorPlataformaCae("dokify", "Dokify");
        var sinDominios = new ProveedorPlataformaCae("otra", "Otra");
        proveedores.ListaProveedores.AddRange([nalanda, dokify, sinDominios]);
        proveedores.ListaDominios.Add(new DominioProveedorPlataformaCae(nalanda.Id, "nalandaglobal.com"));
        proveedores.ListaDominios.Add(new DominioProveedorPlataformaCae(dokify.Id, "dokify.net"));
        proveedores.ListaDominios.Add(new DominioProveedorPlataformaCae(dokify.Id, "dokify.com"));
        var tipo = new TipoDocumento("Certificado", null, false, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        tipos.ListaTiposDocumento.Add(tipo);
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de Trabajo");
        centros.ListaCentros.Add(centro);
        foreach (var proveedor in new[] { nalanda, dokify, sinDominios })
        {
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, $"Portal {proveedor.Nombre}", proveedor.Id, null, null, null);
            var documento = Documento.DeCliente(Guid.NewGuid(), tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            centros.ListaCanalesGestionDocumental.Add(canal);
            documentos.ListaDocumentos.Add(documento);
            documentos.ListaAcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(documento.Id, canal.Id));
        }
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(documentos, centros, proveedores,
            new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), tipos, new AlcanceDatosServiceFalso());

        var sinPedirlos = await handler.Handle(new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: true), CancellationToken.None);
        sinPedirlos.Should().OnlyContain(p => p.Dominios == null || p.Dominios.Count == 0,
            "los dominios son opt-in: Bandeja y Mi trabajo no pagan una consulta más");

        var resultado = await handler.Handle(
            new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: true, IncluirDominios: true), CancellationToken.None);

        resultado.Should().HaveCount(3);
        resultado.Single(p => p.ProveedorCodigo == "nalanda").Dominios.Should().BeEquivalentTo(["nalandaglobal.com"]);
        resultado.Single(p => p.ProveedorCodigo == "dokify").Dominios.Should().BeEquivalentTo(["dokify.com", "dokify.net"]);
        resultado.Single(p => p.ProveedorCodigo == "otra").Dominios.Should().BeEmpty("sin dominios en el catálogo la extensión no ofrece nada de él");
        JsonSerializer.Serialize(resultado[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Contain("\"dominios\":[", "la extensión lee `dominios` del JSON");
    }

    [Fact]
    public async Task Los_proveedores_con_dominios_solapados_viajan_con_su_nombre_aunque_no_tengan_documentos()
    {
        // Mezcla de dominios de la siembra (ProveedorPlataformaCaeSeedData) y un caso sintético. Reales de la
        // siembra: Twind con app.twind.io, CTAIMACAE (legacy) con ctaimacae.net, y Sabentis y Quirón Prevención
        // comparten quironprevencion.com. Sintético: en la siembra ctaima.com es solo de Twind; aquí se le da
        // además a un Proveedor «CTAIMA» (hipotético) para ejercitar el solape que el piloto podría dar de alta.
        var documentos = new DocumentosQueryContextFalso();
        var centros = new CentrosQueryContextFalso();
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var tipos = new TiposDocumentoQueryContextFalso();
        var twind = new ProveedorPlataformaCae("twind", "Twind");
        var ctaima = new ProveedorPlataformaCae("ctaima", "CTAIMA");   // sintético: no existe en la siembra
        var legacy = new ProveedorPlataformaCae("ctaimacae-legacy", "CTAIMACAE (legacy)");
        var sabentis = new ProveedorPlataformaCae("sabentis", "Sabentis");
        var quiron = new ProveedorPlataformaCae("quiron-prevencion", "Quirón Prevención");
        proveedores.ListaProveedores.AddRange([twind, ctaima, legacy, sabentis, quiron]);
        foreach (var (proveedor, dominio) in new[]
        {
            (twind, "app.twind.io"), (twind, "ctaima.com"), (ctaima, "ctaima.com"), (legacy, "ctaimacae.net"),
            (sabentis, "sabentis.com"), (sabentis, "quironprevencion.com"), (quiron, "quironprevencion.com"),
        })
            proveedores.ListaDominios.Add(new DominioProveedorPlataformaCae(proveedor.Id, dominio));
        var tipo = new TipoDocumento("Certificado", null, false, 1, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        tipos.ListaTiposDocumento.Add(tipo);
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de Trabajo");
        centros.ListaCentros.Add(centro);
        // Solo Twind, Sabentis y el legacy tienen documentos pendientes: CTAIMA y Quirón no.
        foreach (var proveedor in new[] { twind, sabentis, legacy })
        {
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, $"Portal {proveedor.Nombre}", proveedor.Id, null, null, null);
            var documento = Documento.DeCliente(Guid.NewGuid(), tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            centros.ListaCanalesGestionDocumental.Add(canal);
            documentos.ListaDocumentos.Add(documento);
            documentos.ListaAcreditacionesDocumentoPlataforma.Add(new AcreditacionDocumentoPlataforma(documento.Id, canal.Id));
        }
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(documentos, centros, proveedores,
            new TrabajadoresQueryContextFalso(), new EmpresasQueryContextFalso(), tipos, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(
            new ObtenerAcreditacionesPorProveedorQuery(SoloSubiblesExtension: true, IncluirDominios: true), CancellationToken.None);

        var delTwind = resultado.Single(p => p.ProveedorCodigo == "twind");
        delTwind.ProveedoresSolapados.Should().ContainSingle().Which.Should().Match<ProveedorSolapadoDto>(
            s => s.Nombre == "CTAIMA" && s.ProveedorPlataformaCaeId == ctaima.Id && s.Dominios.SequenceEqual(new[] { "ctaima.com" }),
            "CTAIMA comparte ctaima.com con Twind aunque no tenga documentos pendientes, y la extensión tiene que poder nombrarlo");
        resultado.Single(p => p.ProveedorCodigo == "sabentis").ProveedoresSolapados!.Select(s => s.Nombre)
            .Should().BeEquivalentTo(["Quirón Prevención"]);
        resultado.Single(p => p.ProveedorCodigo == "ctaimacae-legacy").ProveedoresSolapados
            .Should().BeEmpty("ctaimacae.net no se solapa con ctaima.com: son dominios distintos, no subdominios");

        // La forma que lee la extensión (content.js y background.js): proveedoresSolapados[] con
        // proveedorPlataformaCaeId, nombre y dominios. Renombrar una propiedad rompería el desempate en silencio.
        var json = JsonSerializer.SerializeToElement(
            resultado.Single(p => p.ProveedorCodigo == "sabentis"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var solapado = json.GetProperty("proveedoresSolapados").EnumerateArray().Single();
        solapado.GetProperty("proveedorPlataformaCaeId").GetGuid().Should().Be(quiron.Id);
        solapado.GetProperty("nombre").GetString().Should().Be("Quirón Prevención");
        solapado.GetProperty("dominios").EnumerateArray().Select(d => d.GetString())
            .Should().BeEquivalentTo(["quironprevencion.com"]);
    }

    [Fact]
    public void Dos_dominios_se_solapan_si_son_iguales_o_uno_es_subdominio_del_otro_y_no_por_acabar_igual()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var catalogo = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [a] = ["ejemplo.test"],
            [b] = ["app.ejemplo.test"],       // subdominio del de A: se solapan, en los dos sentidos
            [c] = ["otroejemplo.test"],       // acaba igual que el de A, pero no es subdominio
        };

        ResolucionProveedorPlataformaCaeService.ProveedoresSolapados(a, catalogo).Should().BeEquivalentTo([b]);
        ResolucionProveedorPlataformaCaeService.ProveedoresSolapados(b, catalogo).Should().BeEquivalentTo([a]);
        ResolucionProveedorPlataformaCaeService.ProveedoresSolapados(c, catalogo).Should().BeEmpty();
        ResolucionProveedorPlataformaCaeService.ProveedoresSolapados(Guid.NewGuid(), catalogo).Should().BeEmpty();
    }
    [Fact]
    public void El_dto_no_expone_el_dni_del_trabajador()
    {
        var dto = new AcreditacionDrillDownDto(Guid.NewGuid(), Guid.NewGuid(), "Nombre", "Tipo",
            EstadoAcreditacion.PendienteDeSubir, null);
        typeof(AcreditacionDrillDownDto).GetProperty("TrabajadorDni").Should().BeNull();
        JsonSerializer.Serialize(dto).Should().NotContain("TrabajadorDni");
    }
}
