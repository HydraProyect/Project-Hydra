using System.Text;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Las operaciones de Dividir del protocolo con el proceso aparte: lo que el proceso web acepta del hijo (apertura y
/// tramo) y lo que el hijo acepta como petición de tramo. Una respuesta incoherente es <c>null</c> (se trata como ilegible),
/// nunca un tramo que pase de su cota ni algo que no empiece como un PDF.
/// </summary>
public class ProtocoloDivisionPdfTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%tramo de prueba\n%%EOF");

    private static byte[] Tramo(Result<byte[]> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirTramo(salida, resultado);
        return salida.ToArray();
    }

    private static byte[] Apertura(Result<(int, bool)> resultado)
    {
        using var salida = new MemoryStream();
        ProtocoloProcesoHerramientasPdf.EscribirApertura(salida, resultado);
        return salida.ToArray();
    }

    private static async Task<ProtocoloProcesoHerramientasPdf.Peticion?> IdaYVueltaAsync(int inicial, int final, long cota)
    {
        using var canal = new MemoryStream();
        await ProtocoloProcesoHerramientasPdf.EscribirPeticionTramoAsync(
            canal, Pdf, new ProtocoloProcesoHerramientasPdf.ParametrosTramo(inicial, final, cota), CancellationToken.None);
        canal.Position = 0;
        return ProtocoloProcesoHerramientasPdf.LeerPeticion(canal);
    }

    [Fact]
    public void Un_tramo_dentro_de_su_cota_se_lee_entero()
    {
        var leido = ProtocoloProcesoHerramientasPdf.LeerTramo(Tramo(Result.Exito(Pdf)), cota: Pdf.Length);

        leido!.Valor.Should().Equal(Pdf);
    }

    [Fact]
    public void Un_tramo_que_pasa_de_su_cota_no_se_acepta_aunque_el_hijo_lo_mande()
    {
        ProtocoloProcesoHerramientasPdf.LeerTramo(Tramo(Result.Exito(Pdf)), cota: Pdf.Length - 1).Should().BeNull();
    }

    [Fact]
    public void Un_tramo_que_no_empieza_como_un_PDF_o_llega_cortado_no_se_acepta()
    {
        var noPdf = Encoding.ASCII.GetBytes("<html>no es un PDF</html>");
        var completo = Tramo(Result.Exito(Pdf));

        ProtocoloProcesoHerramientasPdf.LeerTramo(Tramo(Result.Exito(noPdf)), cota: 1000).Should().BeNull();
        ProtocoloProcesoHerramientasPdf.LeerTramo(completo[..^1], cota: 1000).Should().BeNull();
        ProtocoloProcesoHerramientasPdf.LeerTramo(completo.Concat(new byte[] { 0 }).ToArray(), cota: 1000).Should().BeNull();
    }

    [Fact]
    public void Un_fallo_del_tramo_llega_con_su_codigo()
    {
        var fallo = Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.DemasiadoGrande, "grande"));

        ProtocoloProcesoHerramientasPdf.LeerTramo(Tramo(fallo), cota: 1000)!.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
    }

    [Fact]
    public void La_apertura_se_lee_con_sus_paginas_y_su_firma_y_rechaza_lo_incoherente()
    {
        var bytes = Apertura(Result.Exito((3, true)));

        ProtocoloProcesoHerramientasPdf.LeerApertura(bytes)!.Valor.Should().Be((3, true));
        ProtocoloProcesoHerramientasPdf.LeerApertura(bytes[..^1]).Should().BeNull();

        var firmaFalsa = (byte[])bytes.Clone();
        firmaFalsa[^1] = 2;
        ProtocoloProcesoHerramientasPdf.LeerApertura(firmaFalsa).Should().BeNull("la firma es un sí o un no");
        ProtocoloProcesoHerramientasPdf.LeerApertura(Apertura(Result.Exito((0, false)))).Should().BeNull("un PDF sin páginas no se divide");
    }

    [Fact]
    public async Task La_peticion_de_tramo_viaja_entera_y_el_hijo_rechaza_parametros_fuera_de_rango()
    {
        var valida = await IdaYVueltaAsync(2, 3, 5000);

        valida!.Operacion.Should().Be(ProtocoloProcesoHerramientasPdf.OperacionExtraerTramo);
        valida.Tramo.Should().Be(new ProtocoloProcesoHerramientasPdf.ParametrosTramo(2, 3, 5000));
        valida.Pdfs.Should().ContainSingle().Which.Should().Equal(Pdf);

        (await IdaYVueltaAsync(3, 2, 5000)).Should().BeNull("la página final no puede ir antes que la inicial");
        (await IdaYVueltaAsync(0, 2, 5000)).Should().BeNull("las páginas empiezan en 1");
        (await IdaYVueltaAsync(1, 1, 0)).Should().BeNull("una cota vacía no es una cota");
        (await IdaYVueltaAsync(1, 1, ProtocoloProcesoHerramientasPdf.MaximaCotaTramo + 1)).Should().BeNull(
            "ninguna cota real pasa de la del original más grande");
    }
}
