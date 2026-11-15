using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Inbound.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// ADR-013 M2 (K-3): <see cref="IAlmacenOriginalesInbound"/> deja de implementarlo el almacén de disco del Núcleo y lo
/// implementa <see cref="AdaptadorAlmacenOriginalesInbound"/>, en el módulo Inbound, sobre
/// <see cref="IAlmacenArchivosDelTenantPropietario"/>. Es la única lectura del almacenamiento que cruza Tenants: abre, desde
/// la petición del Tenant del Cliente empresarial, un original del Tenant de la Empresa contratista. Lo que fija esta clase:
/// la regla de carpeta del Tenant se aplica antes de tocar el almacén y falla cerrada; la clave física y el cifrado son los
/// de antes (un original escrito en disco con el formato de main se abre sin migrar nada); y la composición resuelve el
/// puerto al adaptador una sola vez. Las garantías de descifrado y alerta del disco siguen en <c>DiskFileStorageServiceTests</c>
/// (IntegrationTests), que ya pasan por el adaptador.
/// </summary>
public sealed class AdaptadorAlmacenOriginalesInboundTests : IDisposable
{
    private static readonly byte[] MarcaV2 = "CAEBLOBv2:"u8.ToArray();

    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"caemanager-originales-inbound-{Guid.NewGuid():N}");
    private readonly Guid _lector = Guid.NewGuid();      // Tenant del Cliente empresarial: el activo de la petición
    private readonly Guid _contratista = Guid.NewGuid(); // Tenant propietario del original: el de la Empresa contratista
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        if (Directory.Exists(_ruta)) Directory.Delete(_ruta, recursive: true);
    }

    private DiskFileStorageService Disco(Guid? tenantActivo) =>
        new(Options.Create(new DiskFileStorageServiceOptions { Ruta = _ruta }), null!,
            new TenantActualAmbiental { TenantId = tenantActivo }, _proteccion, new AlertaInerte(),
            NullLogger<DiskFileStorageService>.Instance,
            new PresupuestoMemoriaDocumentos(Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System));

    private static ArchivoInboundDto Apertura(Guid tenantPropietario, string? referencia) =>
        new(Guid.NewGuid(), tenantPropietario, referencia, SoloConclusionAptitud: false);

    private static async Task<byte[]> LeerAsync(Stream flujo)
    {
        await using (flujo)
        {
            using var copia = new MemoryStream();
            await flujo.CopyToAsync(copia);
            return copia.ToArray();
        }
    }

    [Fact]
    public async Task Abre_el_original_con_la_carpeta_y_la_clave_del_Tenant_propietario_y_no_con_las_del_activo()
    {
        var contenido = "original de la Empresa contratista"u8.ToArray();
        var referencia = await Disco(_contratista).GuardarAsync(new MemoryStream(contenido), "aptitud.pdf");
        var discoDelLector = Disco(_lector);

        (await LeerAsync(await new AdaptadorAlmacenOriginalesInbound(discoDelLector).AbrirOriginalAsync(Apertura(_contratista, referencia))))
            .Should().Equal(contenido);

        await FluentActions.Awaiting(() => discoDelLector.AbrirAsync(referencia))
            .Should().ThrowAsync<FileNotFoundException>("control: con el Tenant activo, el del lector, no se abre");
        await FluentActions.Awaiting(() => new AdaptadorAlmacenOriginalesInbound(discoDelLector).AbrirOriginalAsync(Apertura(_lector, referencia)))
            .Should().ThrowAsync<FileNotFoundException>("control: con el Tenant del lector como propietario tampoco");
    }

    /// <summary>
    /// La clave física y el cifrado no cambian: el fichero se escribe aquí a mano, con la ruta (<c>{raíz}/{tenant:N}/{nombre}</c>),
    /// la marca y el propósito de Data Protection que usa el código de main, sin pasar por <see cref="DiskFileStorageService"/>.
    /// Si el adaptador o el almacén resolvieran otra ruta o derivaran otra clave, los originales ya guardados dejarían de abrirse.
    /// Cubre también el legado v1 sin marca, que sigue sirviéndose sin migrar.
    /// </summary>
    [Fact]
    public async Task Un_original_escrito_con_la_ruta_y_el_cifrado_de_main_se_abre_sin_migrar_nada()
    {
        var carpeta = Path.Combine(_ruta, _contratista.ToString("N"));
        Directory.CreateDirectory(carpeta);
        var claroV2 = "original v2"u8.ToArray();
        var claroV1 = "original v1 sin marca"u8.ToArray();
        var nombreV2 = $"{Guid.NewGuid():N}.pdf";
        var nombreV1 = $"{Guid.NewGuid():N}.pdf";
        var cifradoV2 = _proteccion.CreateProtector("CaeManager.Archivos.v2", _contratista.ToString("N")).Protect(claroV2);
        byte[] enDiscoV2 = [.. MarcaV2, .. cifradoV2];
        await File.WriteAllBytesAsync(Path.Combine(carpeta, nombreV2), enDiscoV2);
        await File.WriteAllBytesAsync(Path.Combine(carpeta, nombreV1), _proteccion.CreateProtector("CaeManager.Archivos.v1").Protect(claroV1));
        var adaptador = new AdaptadorAlmacenOriginalesInbound(Disco(_lector));

        (await LeerAsync(await adaptador.AbrirOriginalAsync(Apertura(_contratista, $"{_contratista:N}/{nombreV2}")))).Should().Equal(claroV2);
        (await LeerAsync(await adaptador.AbrirOriginalAsync(Apertura(_contratista, $"{_contratista:N}/{nombreV1}")))).Should().Equal(claroV1);
    }

    private IEnumerable<(Guid Tenant, string? Referencia, string Motivo)> AperturasQueNoSonDeLaCarpetaDelTenant()
    {
        var c = _contratista.ToString("N");
        var l = _lector.ToString("N");
        return
        [
            (_contratista, $"{l}/x.pdf", "la carpeta es de otro Tenant"),
            (_lector, $"{c}/x.pdf", "el Tenant indicado no es el de la carpeta"),
            (Guid.Empty, $"{c}/x.pdf", "sin Tenant propietario"),
            (_contratista, null, "sin referencia"),
            (_contratista, "", "referencia vacía"),
            (_contratista, $"{c}/", "solo la carpeta"),
            (_contratista, "x.pdf", "sin carpeta"),
            (_contratista, $"{c}/../{l}/x.pdf", "un salto de carpeta con /"),
            (_contratista, $"{c}/..\\{l}\\x.pdf", "un salto de carpeta con barra invertida"),
            (_contratista, $"{c}/..", "solo un salto"),
            (_contratista, $"{c}\\x.pdf", "la barra invertida no separa la carpeta del Tenant"),
            (_contratista, $"..\\{c}/x.pdf", "un salto antes de la carpeta"),
            (_contratista, $"{c}/sub/x.pdf", "una subcarpeta con /"),
            (_contratista, $"{_contratista.ToString("N").ToUpperInvariant()}/x.pdf", "la carpeta con otra grafía"),
            (_contratista, $"/{c}/x.pdf", "una ruta absoluta"),
        ];
    }

    [Fact]
    public async Task Una_apertura_que_no_es_de_la_carpeta_del_Tenant_propietario_falla_cerrada_antes_de_tocar_el_almacen()
    {
        var almacen = new AlmacenEspia();
        var adaptador = new AdaptadorAlmacenOriginalesInbound(almacen);

        foreach (var (tenant, referencia, motivo) in AperturasQueNoSonDeLaCarpetaDelTenant())
        {
            await FluentActions.Awaiting(() => adaptador.AbrirOriginalAsync(Apertura(tenant, referencia)))
                .Should().ThrowAsync<FileNotFoundException>(motivo);
        }

        almacen.Aperturas.Should().BeEmpty("la regla de carpeta se aplica antes de cualquier lectura");
    }

    /// <summary>
    /// Defensa en profundidad: la lectura entre Tenants del Núcleo repite la regla de la carpeta por sí misma, así que un
    /// consumidor que se saltara el adaptador tampoco abriría nada fuera de la carpeta del Tenant indicado. Aquí existe de
    /// verdad un <c>x.pdf</c> cifrado en la carpeta de cada Tenant, y el control abre el de la contratista: los rechazos no se
    /// deben a que falte el fichero.
    /// </summary>
    [Fact]
    public async Task La_lectura_entre_Tenants_del_Nucleo_tambien_falla_cerrada_por_si_misma()
    {
        foreach (var tenant in new[] { _contratista, _lector })
        {
            var carpeta = Path.Combine(_ruta, tenant.ToString("N"));
            Directory.CreateDirectory(carpeta);
            byte[] enDisco = [.. MarcaV2, .. _proteccion.CreateProtector("CaeManager.Archivos.v2", tenant.ToString("N")).Protect("x"u8.ToArray())];
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "x.pdf"), enDisco);
        }
        var disco = Disco(_lector);

        (await LeerAsync(await disco.AbrirDelTenantPropietarioAsync($"{_contratista:N}/x.pdf", _contratista)))
            .Should().Equal("x"u8.ToArray(), "control: la apertura válida abre el fichero que sí existe");
        foreach (var (tenant, referencia, motivo) in AperturasQueNoSonDeLaCarpetaDelTenant())
        {
            await FluentActions.Awaiting(() => disco.AbrirDelTenantPropietarioAsync(referencia!, tenant))
                .Should().ThrowAsync<FileNotFoundException>(motivo);
        }
    }

    /// <summary>
    /// Deuda conocida de <see cref="ClavesDeAlmacen"/>, que esta PR no cambia: <c>{tenant:N}/sub\archivo</c> pasa la regla
    /// (la barra invertida no se trata como separador). Llega al almacén, que resuelve el nombre con
    /// <see cref="Path.GetFileName(string?)"/> dentro de la carpeta del Tenant indicado: nunca abre el archivo de otro
    /// Tenant, aunque la referencia lo nombre detrás de la barra invertida.
    /// </summary>
    [Fact]
    public async Task La_subcarpeta_con_barra_invertida_llega_al_almacen_pero_no_sale_de_la_carpeta_del_Tenant()
    {
        var delLector = await Disco(_lector).GuardarAsync(new MemoryStream("del lector"u8.ToArray()), "x.pdf");
        var referencia = $"{_contratista:N}/{_lector:N}\\{Path.GetFileName(delLector)}";
        OriginalesInbound.EsDelTenant(referencia, _contratista).Should().BeTrue("es la deuda conocida, sin cambio aquí");

        await FluentActions.Awaiting(() => new AdaptadorAlmacenOriginalesInbound(Disco(_lector)).AbrirOriginalAsync(Apertura(_contratista, referencia)))
            .Should().ThrowAsync<FileNotFoundException>("el archivo del lector no está en la carpeta de la contratista");
    }

    [Fact]
    public async Task Una_apertura_valida_llega_al_almacen_con_la_misma_referencia_el_mismo_Tenant_y_la_misma_cancelacion()
    {
        var almacen = new AlmacenEspia();
        var referencia = $"{_contratista:N}/{Guid.NewGuid():N}.pdf";
        using var cancelacion = new CancellationTokenSource();

        await using var flujo = await new AdaptadorAlmacenOriginalesInbound(almacen)
            .AbrirOriginalAsync(Apertura(_contratista, referencia), cancelacion.Token);

        almacen.Aperturas.Should().ContainSingle().Which.Should().Be((referencia, _contratista, cancelacion.Token));
        flujo.Should().BeSameAs(almacen.Devuelto, "el adaptador no copia ni envuelve el flujo: la reserva de memoria la cierra quien lo abre");
    }

    /// <summary>
    /// La composición resuelve <see cref="IAlmacenOriginalesInbound"/> al adaptador, una sola vez, sobre la misma instancia
    /// del almacén de disco que <see cref="IFileStorageService"/> en el mismo ámbito. Que ninguna otra pieza de <c>src</c>
    /// (Program.cs incluido) lo registre lo fija <c>AlmacenDeDiscoSoloPorSusPuertosTests</c> con el barrido de identificadores.
    /// </summary>
    [Fact]
    public async Task La_composicion_resuelve_el_puerto_de_originales_Inbound_al_adaptador_una_sola_vez()
    {
        var servicios = new ServiceCollection();
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AlmacenamientoArchivos:Ruta"] = _ruta })
            .Build();
        servicios.AddInfrastructure(configuracion, new EntornoFalso());
        // Lo que el host web aporta y AddInfrastructure no registra.
        servicios.AddLogging();
        servicios.AddSingleton<IHostEnvironment>(new EntornoFalso());
        servicios.AddSingleton(_proteccion);
        servicios.AddScoped<ITenantActual>(_ => new TenantActualAmbiental { TenantId = _lector });

        servicios.Where(d => d.ServiceType == typeof(IAlmacenOriginalesInbound)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be(typeof(AdaptadorAlmacenOriginalesInbound));

        using var proveedor = servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var ambito = proveedor.CreateScope();
        ambito.ServiceProvider.GetServices<IAlmacenOriginalesInbound>().Should().ContainSingle()
            .Which.Should().BeOfType<AdaptadorAlmacenOriginalesInbound>();
        ambito.ServiceProvider.GetRequiredService<IAlmacenArchivosDelTenantPropietario>()
            .Should().BeSameAs(ambito.ServiceProvider.GetRequiredService<IFileStorageService>(), "la misma instancia y la misma clave por Tenant");

        // Y abre de verdad por la composición: un original de la contratista desde el ámbito del lector.
        var referencia = await Disco(_contratista).GuardarAsync(new MemoryStream("por la composición"u8.ToArray()), "x.pdf");
        (await LeerAsync(await ambito.ServiceProvider.GetRequiredService<IAlmacenOriginalesInbound>()
            .AbrirOriginalAsync(Apertura(_contratista, referencia)))).Should().Equal("por la composición"u8.ToArray());
    }

    private sealed class AlmacenEspia : IAlmacenArchivosDelTenantPropietario
    {
        public List<(string Referencia, Guid Tenant, CancellationToken Cancelacion)> Aperturas { get; } = [];
        public Stream Devuelto { get; } = new MemoryStream([1, 2, 3]);

        public Task<Stream> AbrirDelTenantPropietarioAsync(string identificador, Guid tenantPropietarioId, CancellationToken cancellationToken = default)
        {
            Aperturas.Add((identificador, tenantPropietarioId, cancellationToken));
            return Task.FromResult(Devuelto);
        }
    }

    private sealed class EntornoFalso : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class AlertaInerte : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new SinAmbito();

        private sealed class SinAmbito : IDisposable
        {
            public void Dispose() { }
        }
    }
}
