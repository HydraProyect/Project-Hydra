using CaeManager.Application.Common;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace CaeManager.Web.Tests;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): <see cref="DiskFileStorageService.GuardarAsync"/>
/// reserva del presupuesto de memoria del proceso el claro y el cifrado antes de cargarlos y lo devuelve
/// siempre, sin cambiar lo que escribe: marca <c>CAEBLOBv2:</c> seguida del payload de Data Protection
/// del Tenant, escritos directamente en el temporal. Las garantías de aislamiento y de formato legado
/// siguen en <c>DiskFileStorageServiceTests</c> (IntegrationTests).
/// </summary>
public sealed class DiskFileStorageServicePresupuestoMemoriaTests : IDisposable
{
    private static readonly byte[] Marca = "CAEBLOBv2:"u8.ToArray();

    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"caemanager-presupuesto-{Guid.NewGuid():N}");
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        if (Directory.Exists(_ruta)) Directory.Delete(_ruta, recursive: true);
    }

    private static PresupuestoMemoriaDocumentos Presupuesto(long maximo = 64L * 1024 * 1024, int esperaMs = 10_000) =>
        new(Options.Create(new OpcionesPresupuestoMemoriaDocumentos
        {
            MaximoBytes = maximo,
            EsperaMaxima = TimeSpan.FromMilliseconds(esperaMs)
        }), TimeProvider.System);

    private DiskFileStorageService Servicio(IPresupuestoMemoriaDocumentos presupuesto) =>
        new(Options.Create(new DiskFileStorageServiceOptions { Ruta = _ruta }), null!,
            new TenantActualAmbiental { TenantId = _tenant }, _proteccion, new AlertaInerte(),
            NullLogger<DiskFileStorageService>.Instance, presupuesto);

    private string[] FicherosEnDisco() =>
        Directory.Exists(_ruta) ? Directory.GetFiles(_ruta, "*", SearchOption.AllDirectories) : [];

    private static byte[] Contenido(int bytes)
    {
        var contenido = new byte[bytes];
        Random.Shared.NextBytes(contenido);
        return contenido;
    }

    [Fact]
    public async Task Guardar_escribe_la_marca_y_el_cifrado_del_tenant_y_devuelve_toda_la_reserva()
    {
        var presupuesto = new PresupuestoContado(Presupuesto());
        var contenido = Contenido(300 * 1024);

        var identificador = await Servicio(presupuesto).GuardarAsync(new MemoryStream(contenido), "informe.pdf");

        var enDisco = await File.ReadAllBytesAsync(FicherosEnDisco().Should().ContainSingle().Subject);
        enDisco.AsSpan(0, Marca.Length).ToArray().Should().Equal(Marca, "el formato v2 no cambia");
        _proteccion.CreateProtector("CaeManager.Archivos.v2", _tenant.ToString("N")).Unprotect(enDisco[Marca.Length..])
            .Should().Equal(contenido, "detrás de la marca va el payload protegido con la clave del Tenant, sin más");
        await using (var abierto = await Servicio(presupuesto).AbrirAsync(identificador))
        {
            using var leido = new MemoryStream();
            await abierto.CopyToAsync(leido);
            leido.ToArray().Should().Equal(contenido);
        }

        presupuesto.PedidoAcumulado.Should().BeGreaterThanOrEqualTo(2L * contenido.Length, "el claro y el cifrado se reservan juntos antes de existir");
        presupuesto.Interno.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Un_origen_sin_longitud_se_reserva_por_trozos_y_se_guarda_igual()
    {
        var presupuesto = new PresupuestoContado(Presupuesto());
        var contenido = Contenido(3 * 1024 * 1024 + 17);

        var identificador = await Servicio(presupuesto).GuardarAsync(new FlujoSinLongitud(contenido), "foto.jpg");

        await using (var abierto = await Servicio(presupuesto).AbrirAsync(identificador))
        {
            using var leido = new MemoryStream();
            await abierto.CopyToAsync(leido);
            leido.ToArray().Should().Equal(contenido);
        }

        presupuesto.Pedidos.Should().BeGreaterThan(2, "sin longitud declarada se reserva a medida que crece");
        presupuesto.PedidoAcumulado.Should().BeGreaterThanOrEqualTo(contenido.Length);
        presupuesto.Interno.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_origen_falla_a_mitad_la_reserva_vuelve_y_no_queda_ni_temporal()
    {
        var presupuesto = Presupuesto();

        var guardar = () => Servicio(presupuesto).GuardarAsync(new FlujoQueFalla(Contenido(2 * 1024 * 1024)), "roto.pdf");

        await guardar.Should().ThrowExactlyAsync<IOException>();
        presupuesto.Reservado.Should().Be(0);
        FicherosEnDisco().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_origen_que_trae_mas_de_la_longitud_que_declara_se_guarda_entero_sin_truncarlo()
    {
        var presupuesto = new PresupuestoContado(Presupuesto());
        var contenido = Contenido(2 * 1024 * 1024);

        var identificador = await Servicio(presupuesto).GuardarAsync(new FlujoQueMienteLongitud(contenido, 1024), "largo.pdf");

        await using (var abierto = await Servicio(presupuesto).AbrirAsync(identificador))
        {
            using var leido = new MemoryStream();
            await abierto.CopyToAsync(leido);
            leido.ToArray().Should().Equal(contenido, "lo que llega de más también se reserva y se guarda");
        }

        presupuesto.PedidoAcumulado.Should().BeGreaterThanOrEqualTo(2L * contenido.Length);
        presupuesto.Interno.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Abrir_conserva_la_reserva_hasta_cerrar_y_devuelve_los_bytes()
    {
        var presupuesto = Presupuesto();
        var contenido = Contenido(12345);
        var servicio = Servicio(presupuesto);
        var identificador = await servicio.GuardarAsync(new MemoryStream(contenido), "archivo.pdf");
        var contado = new PresupuestoContado(presupuesto);
        var flujo = await Servicio(contado).AbrirAsync(identificador);
        contado.PedidoAcumulado.Should().BeGreaterThanOrEqualTo(3L * contenido.Length, "se reserva el pico del descifrado antes de leer");
        presupuesto.Reservado.Should().BeGreaterThan(contenido.Length)
            .And.BeLessThan(2L * contenido.Length, "tras descifrar el flujo retiene solo el claro");
        using var copia = new MemoryStream();
        await flujo.CopyToAsync(copia);
        copia.ToArray().Should().Equal(contenido);
        await flujo.DisposeAsync();
        flujo.Dispose();
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Una_descarga_interrumpida_devuelve_la_reserva_al_cerrar()
    {
        var presupuesto = Presupuesto();
        var servicio = Servicio(presupuesto);
        var identificador = await servicio.GuardarAsync(new MemoryStream(Contenido(4096)), "archivo.pdf");
        var descargar = async () =>
        {
            await using var flujo = await servicio.AbrirAsync(identificador);
            var cabecera = new byte[100];
            await flujo.ReadExactlyAsync(cabecera);
            throw new IOException("descarga interrumpida");
        };
        await descargar.Should().ThrowExactlyAsync<IOException>();
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task El_finalizador_devuelve_la_reserva_de_un_flujo_abandonado()
    {
        var presupuesto = Presupuesto();
        var servicio = Servicio(presupuesto);
        var identificador = await servicio.GuardarAsync(new MemoryStream(Contenido(4096)), "archivo.pdf");
        // En el grupo de hilos, no en el contexto de sincronización de xunit: AbrirYAbandonar
        // espera de forma síncrona y bloquearía ese contexto si solo tuviera un hilo.
        var referencia = await Task.Run(() => AbrirYAbandonar(servicio, identificador, presupuesto));

        // La referencia local del llamador ya no retiene el flujo, tampoco en Debug. Las tareas
        // asíncronas de apertura aún pueden conservarlo transitoriamente.
        for (var intento = 0; intento < 5 && presupuesto.Reservado != 0; intento++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        referencia.IsAlive.Should().BeFalse();
        presupuesto.Reservado.Should().Be(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbrirYAbandonar(
        DiskFileStorageService servicio, string identificador, PresupuestoMemoriaDocumentos presupuesto)
    {
        var flujo = servicio.AbrirAsync(identificador).GetAwaiter().GetResult();
        var referencia = new WeakReference(flujo);
        presupuesto.Reservado.Should().BeGreaterThan(0);
        GC.KeepAlive(flujo);
        return referencia;
    }

    [Fact]
    public async Task Abrir_original_inbound_reserva_y_devuelve_los_bytes()
    {
        var presupuesto = Presupuesto();
        var servicio = Servicio(presupuesto);
        var contenido = Contenido(4096);
        var identificador = await servicio.GuardarAsync(new MemoryStream(contenido), "archivo.pdf");
        var archivo = new CaeManager.Application.ContratosAcceso.LecturaInbound.ArchivoInboundDto(Guid.NewGuid(), _tenant, identificador, false);
        await using (var flujo = await servicio.AbrirOriginalAsync(archivo))
        {
            presupuesto.Reservado.Should().BeGreaterThan(0);
            using var copia = new MemoryStream();
            await flujo.CopyToAsync(copia);
            copia.ToArray().Should().Equal(contenido);
        }
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Abrir_devuelve_la_reserva_si_falla_el_descifrado()
    {
        var presupuesto = Presupuesto();
        var servicio = Servicio(presupuesto);
        var identificador = await servicio.GuardarAsync(new MemoryStream(Contenido(1024)), "archivo.pdf");
        var ruta = FicherosEnDisco().Should().ContainSingle().Subject;
        var bytes = await File.ReadAllBytesAsync(ruta);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(ruta, bytes);
        var abrir = () => servicio.AbrirAsync(identificador);
        await abrir.Should().ThrowExactlyAsync<InvalidDataException>();
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Abrir_sin_hueco_rechaza_sin_perder_la_reserva()
    {
        var presupuesto = Presupuesto(maximo: 4 * 1024 * 1024, esperaMs: 100);
        var servicio = Servicio(presupuesto);
        var identificador = await servicio.GuardarAsync(new MemoryStream(Contenido(1024)), "archivo.pdf");
        using var ocupado = await presupuesto.ReservarAsync(presupuesto.Maximo);
        var abrir = () => servicio.AbrirAsync(identificador);
        await abrir.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        presupuesto.Reservado.Should().Be(presupuesto.Maximo);
    }

    /// <summary>Se puede posicionar, pero declara menos de lo que entrega.</summary>
    private sealed class FlujoQueMienteLongitud(byte[] contenido, long longitudDeclarada) : Stream
    {
        private readonly MemoryStream _interno = new(contenido);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => longitudDeclarada;
        public override long Position { get => _interno.Position; set => _interno.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _interno.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _interno.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_se_rechaza_sin_escribir_nada_y_sin_quedarse_reserva()
    {
        var presupuesto = Presupuesto(maximo: 4L * 1024 * 1024, esperaMs: 100);
        var ocupado = await presupuesto.ReservarAsync(4L * 1024 * 1024);

        var guardar = () => Servicio(presupuesto).GuardarAsync(new MemoryStream(Contenido(1024)), "bloqueado.pdf");

        await guardar.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        FicherosEnDisco().Should().BeEmpty();
        presupuesto.Reservado.Should().Be(4L * 1024 * 1024, "solo la reserva que lo bloqueaba");
        presupuesto.EnEspera.Should().Be(0);

        ocupado.Dispose();
        await Servicio(presupuesto).GuardarAsync(new MemoryStream(Contenido(1024)), "con-hueco.pdf");
        FicherosEnDisco().Should().ContainSingle("control: con hueco se guarda");
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Guardados_concurrentes_esperan_su_turno_sin_pasar_del_presupuesto()
    {
        const int tamano = 1024 * 1024;
        // Caben dos a la vez (claro, cifrado y copia intermedia: tres veces cada uno), no los ocho.
        var presupuesto = new PresupuestoContado(Presupuesto(maximo: 7L * tamano));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            Servicio(presupuesto).GuardarAsync(new MemoryStream(Contenido(tamano)), $"lote-{i}.pdf"))))
            .WaitAsync(TimeSpan.FromSeconds(30));

        FicherosEnDisco().Should().HaveCount(8);
        presupuesto.MaximoReservado.Should().BeLessThanOrEqualTo(7L * tamano);
        presupuesto.Interno.Reservado.Should().Be(0);
    }

    /// <summary>Envoltorio que anota lo pedido en total y el máximo reservado que llegó a ver.</summary>
    private sealed class PresupuestoContado(PresupuestoMemoriaDocumentos interno) : IPresupuestoMemoriaDocumentos
    {
        private long _pedidoAcumulado;
        private long _maximoReservado;
        private int _pedidos;

        public PresupuestoMemoriaDocumentos Interno { get; } = interno;
        public long PedidoAcumulado => Interlocked.Read(ref _pedidoAcumulado);
        public long MaximoReservado => Interlocked.Read(ref _maximoReservado);
        public int Pedidos => Volatile.Read(ref _pedidos);

        // Explícita: esta clase ya tiene un método Maximo(ref, long) auxiliar.
        long IPresupuestoMemoriaDocumentos.Maximo => Interno.Maximo;

        public async ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
        {
            var reserva = await Interno.ReservarAsync(bytes, cancellationToken);
            Interlocked.Increment(ref _pedidos);
            Interlocked.Add(ref _pedidoAcumulado, bytes);
            Maximo(ref _maximoReservado, Interno.Reservado);
            return reserva;
        }

        private static void Maximo(ref long objetivo, long valor)
        {
            long actual;
            while ((actual = Interlocked.Read(ref objetivo)) < valor
                   && Interlocked.CompareExchange(ref objetivo, valor, actual) != actual)
            {
            }
        }
    }

    /// <summary>Como el flujo del navegador: no se puede posicionar ni dice su longitud.</summary>
    private sealed class FlujoSinLongitud(byte[] contenido) : Stream
    {
        private readonly MemoryStream _interno = new(contenido);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _interno.Read(buffer, offset, Math.Min(count, 50_000));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Entrega la mitad y después falla, como una conexión que se corta.</summary>
    private sealed class FlujoQueFalla(byte[] contenido) : Stream
    {
        private int _entregado;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_entregado >= contenido.Length / 2)
                throw new IOException("conexión cortada");
            var n = Math.Min(count, contenido.Length / 2 - _entregado);
            Array.Copy(contenido, _entregado, buffer, offset, n);
            _entregado += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
