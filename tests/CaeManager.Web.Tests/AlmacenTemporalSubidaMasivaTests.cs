using CaeManager.Web.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// S-C-3 (auditoría del 2026-10-28; OWASP API4:2023, ASVS 4.0.3 V12.1.1, CWE-400):
/// la Subida múltiple retenía hasta unos 600 MiB por circuito en <c>byte[]</c>, y
/// cada lote nuevo sumaba otro tanto. Ahora lo retenido vive en temporales con un
/// tope por pantalla, por Tenant de origen y por proceso. Estos casos fijan que lo
/// que no cabe se rechaza sin leerse, que lo que llega de más que lo declarado
/// también descuenta, y que los temporales se borran al soltarlos y al cerrar la pantalla.
/// </summary>
public sealed class AlmacenTemporalSubidaMasivaTests : IDisposable
{
    private readonly TemporalesDePrueba _temporales = new();

    public void Dispose() => _temporales.Dispose();

    [Fact]
    public async Task Lo_que_declara_mas_de_lo_que_queda_se_rechaza_sin_abrir_el_origen()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000);
        using var almacen = cuota.CrearAlmacen(Guid.NewGuid());
        await almacen.GuardarAsync(new byte[600]);
        var aperturas = 0;

        var guardar = () => almacen.GuardarAsync(() => { aperturas++; return new MemoryStream(new byte[500]); },
            tamanoDeclarado: 500, maximoBytes: 10_000);

        await guardar.Should().ThrowExactlyAsync<CuotaSubidaMasivaSuperadaException>();
        aperturas.Should().Be(0, "el excedente se rechaza antes de pedir un solo byte al navegador o al .zip");
        almacen.Reservado.Should().Be(600, "el rechazo no deja reserva colgada");
        Directory.GetFiles(almacen.Directorio).Should().ContainSingle("solo queda el archivo que sí cabía");
    }

    [Fact]
    public async Task Lo_que_el_origen_trae_de_mas_sobre_lo_declarado_tambien_descuenta_y_al_pasarse_se_borra()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000);
        using var almacen = cuota.CrearAlmacen(Guid.NewGuid());

        // Declara 10 bytes y entrega 2000: el tope se aplica a lo leído, trozo a trozo.
        var guardar = () => almacen.GuardarAsync(() => new MemoryStream(new byte[2000]), tamanoDeclarado: 10, maximoBytes: 10_000);

        await guardar.Should().ThrowExactlyAsync<CuotaSubidaMasivaSuperadaException>();
        almacen.Reservado.Should().Be(0);
        cuota.ReservadoGlobal.Should().Be(0);
        Directory.GetFiles(almacen.Directorio).Should().BeEmpty("el temporal a medio escribir se borra");
    }

    [Fact]
    public async Task Un_origen_que_supera_el_maximo_por_archivo_se_corta_aunque_quepa_en_la_pantalla()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1_000_000);
        using var almacen = cuota.CrearAlmacen(Guid.NewGuid());

        var guardar = () => almacen.GuardarAsync(() => new MemoryStream(new byte[300_000]), tamanoDeclarado: 10, maximoBytes: 100_000);

        await guardar.Should().ThrowExactlyAsync<ArchivoTemporalDemasiadoGrandeException>();
        almacen.Reservado.Should().Be(0);
        Directory.GetFiles(almacen.Directorio).Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_que_declara_mas_de_lo_que_trae_devuelve_la_diferencia()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000);
        using var almacen = cuota.CrearAlmacen(Guid.NewGuid());

        var archivo = await almacen.GuardarAsync(() => new MemoryStream(new byte[100]), tamanoDeclarado: 900, maximoBytes: 1000);

        archivo.TamanoBytes.Should().Be(100);
        almacen.Reservado.Should().Be(100, "una declaración inflada no puede bloquear la pantalla");
        (await almacen.LeerAsync(archivo)).Should().HaveCount(100);
    }

    [Fact]
    public async Task Eliminar_borra_el_temporal_y_devuelve_la_reserva_una_sola_vez()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000);
        var tenant = Guid.NewGuid();
        using var almacen = cuota.CrearAlmacen(tenant);
        var uno = await almacen.GuardarAsync(new byte[300]);
        await almacen.GuardarAsync(new byte[200]);

        almacen.Eliminar(uno);
        almacen.Eliminar(uno);

        uno.Eliminado.Should().BeTrue();
        almacen.Reservado.Should().Be(200, "eliminar dos veces no devuelve dos veces");
        cuota.ReservadoDe(tenant).Should().Be(200);
        Directory.GetFiles(almacen.Directorio).Should().ContainSingle();
    }

    [Fact]
    public async Task Cerrar_la_pantalla_borra_todos_sus_temporales_y_devuelve_toda_su_reserva()
    {
        using var cuota = _temporales.Cuota(porCircuito: 10_000);
        var tenant = Guid.NewGuid();
        var almacen = cuota.CrearAlmacen(tenant);
        await almacen.GuardarAsync(new byte[1000]);
        await almacen.GuardarAsync(() => new MemoryStream(new byte[2000]), 2000, 10_000);
        almacen.TryReservar(500).Should().BeTrue("memoria retenida fuera de los ficheros, p. ej. una miniatura");
        Directory.GetFiles(almacen.Directorio).Should().HaveCount(2, "control: había temporales que borrar");

        almacen.Dispose();

        Directory.Exists(almacen.Directorio).Should().BeFalse();
        cuota.ReservadoGlobal.Should().Be(0);
        cuota.ReservadoDe(tenant).Should().Be(0);
        var despues = () => almacen.GuardarAsync(new byte[1]);
        await despues.Should().ThrowAsync<Exception>("una pantalla cerrada no vuelve a escribir");
        Directory.Exists(almacen.Directorio).Should().BeFalse();
    }

    [Fact]
    public async Task Un_temporal_abierto_al_cerrar_la_pantalla_se_borra_al_soltarlo()
    {
        using var cuota = _temporales.Cuota(porCircuito: 10_000);
        var almacen = cuota.CrearAlmacen(Guid.NewGuid());
        var pdf = await almacen.GuardarAsync(new byte[1000]);

        // La confirmación en vuelo: el PDF se está enviando al almacenamiento cuando la pantalla se cierra.
        await using (almacen.AbrirLectura(pdf))
            almacen.Dispose();
        almacen.Eliminar(pdf);

        Directory.Exists(almacen.Directorio).Should().BeFalse();
        cuota.ReservadoGlobal.Should().Be(0, "Dispose ya devolvió la reserva; Eliminar después no resta de nuevo");
    }

    [Fact]
    public async Task El_tope_por_Tenant_lo_comparten_sus_pantallas_y_no_alcanza_a_otro_Tenant()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000, porTenant: 1500, global: 10_000);
        var tenant = Guid.NewGuid();
        using var primera = cuota.CrearAlmacen(tenant);
        using var segunda = cuota.CrearAlmacen(tenant);
        using var deOtroTenant = cuota.CrearAlmacen(Guid.NewGuid());
        await primera.GuardarAsync(new byte[1000]);

        var guardar = () => segunda.GuardarAsync(new byte[600]);

        await guardar.Should().ThrowExactlyAsync<CuotaSubidaMasivaSuperadaException>(
            "cabe en la segunda pantalla, pero no en lo que le queda a su Tenant");
        (await deOtroTenant.GuardarAsync(new byte[600])).TamanoBytes.Should().Be(600);
    }

    [Fact]
    public async Task El_tope_global_lo_comparten_todos_los_Tenants()
    {
        using var cuota = _temporales.Cuota(porCircuito: 1000, porTenant: 1000, global: 1500);
        using var a = cuota.CrearAlmacen(Guid.NewGuid());
        using var b = cuota.CrearAlmacen(Guid.NewGuid());
        await a.GuardarAsync(new byte[1000]);

        var guardar = () => b.GuardarAsync(new byte[600]);

        await guardar.Should().ThrowExactlyAsync<CuotaSubidaMasivaSuperadaException>();
        cuota.ReservadoGlobal.Should().Be(1000);
    }

    [Fact]
    public void Al_arrancar_se_borran_los_temporales_de_un_proceso_terminado_y_no_los_de_uno_vivo()
    {
        using var vivo = _temporales.Cuota(porCircuito: 1000);
        var muerto = Path.Combine(_temporales.Raiz, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(muerto, "pantalla"));
        File.WriteAllBytes(Path.Combine(muerto, "pantalla", "pendiente"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(muerto, ".vivo"), []);
        // Sin testigo y antiguo, pero con un nombre que esta clase nunca crea: la raíz es
        // configurable y podría apuntar por error a un volumen con otros datos.
        var ajeno = Path.Combine(_temporales.Raiz, "documentos");
        Directory.CreateDirectory(ajeno);
        File.WriteAllBytes(Path.Combine(ajeno, "dato"), [1]);
        Directory.SetLastWriteTimeUtc(ajeno, DateTime.UtcNow.AddDays(-1));

        using var siguiente = _temporales.Cuota(porCircuito: 1000);

        Directory.Exists(muerto).Should().BeFalse("su testigo no lo retenía nadie");
        File.Exists(Path.Combine(ajeno, "dato")).Should().BeTrue("solo se borra lo que tiene nombre de subdirectorio de proceso");
        Directory.Exists(vivo.DirectorioProceso).Should().BeTrue("su testigo sigue abierto en exclusiva");
        Directory.Exists(siguiente.DirectorioProceso).Should().BeTrue();
    }

    [Fact]
    public void Los_topes_configurados_admiten_un_archivo_maximo_y_van_de_la_pantalla_al_proceso()
    {
        new OpcionesSubidaMasiva().SonValidas().Should().BeTrue("control: los valores por defecto");
        new OpcionesSubidaMasiva { MaximoBytesPorCircuito = 1024 * 1024 }.SonValidas()
            .Should().BeFalse("la pantalla no podría retener ni un archivo de 10 MB");
        new OpcionesSubidaMasiva { MaximoBytesPorTenant = 100L * 1024 * 1024 }.SonValidas()
            .Should().BeFalse("el Tenant no puede tener menos que una sola pantalla");
        new OpcionesSubidaMasiva { MaximoBytesGlobal = 900L * 1024 * 1024 }.SonValidas()
            .Should().BeFalse("el proceso no puede tener menos que un solo Tenant");
    }
}

/// <summary>Raíz de temporales propia de cada test, borrada al terminar.</summary>
internal sealed class TemporalesDePrueba : IDisposable
{
    public string Raiz { get; } = Path.Combine(Path.GetTempPath(), "talveg-subida-masiva-tests", Guid.NewGuid().ToString("N"));

    private readonly List<CuotaSubidaMasiva> _cuotas = [];

    public CuotaSubidaMasiva Cuota(long porCircuito, long? porTenant = null, long? global = null)
    {
        var cuota = new CuotaSubidaMasiva(Options.Create(new OpcionesSubidaMasiva
        {
            MaximoBytesPorCircuito = porCircuito,
            MaximoBytesPorTenant = porTenant ?? porCircuito * 10,
            MaximoBytesGlobal = global ?? porCircuito * 100,
            DirectorioTemporal = Raiz
        }));
        _cuotas.Add(cuota);
        return cuota;
    }

    public void Dispose()
    {
        foreach (var cuota in _cuotas)
            cuota.Dispose();
        try
        {
            if (Directory.Exists(Raiz))
                Directory.Delete(Raiz, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
