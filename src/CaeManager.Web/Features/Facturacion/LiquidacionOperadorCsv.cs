using System.Globalization;
using System.Text;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Facturacion.LiquidacionesOperadorCae;
using CaeManager.Domain.Facturacion;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Facturacion;

/// <summary>
/// Escribe la liquidación mensual del Operador CAE externo en CSV importable (V-2). Una fila por Tenant beneficiario y
/// concepto, con las unidades, el precio y la base con los que la pantalla «Facturación» valoró el mes
/// (<see cref="FacturacionOperadorModelo"/>): aquí no se calcula nada. Cada fila lleva en su columna «Tipo de documento»
/// que es una liquidación interna sin valor fiscal (D-F3), y el nombre del fichero también: no es una factura, ni se
/// numera como tal. La leyenda no va en una línea suelta, porque rompería la importación. Las columnas sin dato en el
/// modelo (NIF, razón social y tipo de IVA) salen vacías.
///
/// <para>
/// Formato: UTF-8 con BOM (para que la hoja de cálculo reconozca los acentos), separador punto y coma, coma decimal sin
/// separador de miles y fin de línea CRLF (RFC 4180). Un valor que empiece por <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>,
/// tabulador o retorno se antepone con un apóstrofo para que la hoja no lo ejecute como fórmula (inyección en CSV, OWASP
/// ASVS 4.0.3 § 5.3.10 y CWE-1236): el nombre de la Empresa y la referencia de la factura los escribe una persona. Los
/// números se escriben aparte y nunca se alteran.
/// </para>
/// </summary>
public static class LiquidacionOperadorCsv
{
    public const string TipoContenido = "text/csv; charset=utf-8";

    private const char Separador = ';';

    private static readonly NumberFormatInfo FormatoNumero = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = string.Empty };

    public static byte[] Escribir(
        ModeloFacturacion modelo, CobrosLiquidacionOperadorCaeDto cobros, IStringLocalizer<Recursos.TextosFacturacion> textos)
    {
        var texto = new StringBuilder();
        Linea(texto,
        [
            Texto(textos["CsvColDocumento"].Value), Texto(textos["CsvColPeriodo"].Value), Texto(textos["CsvColEstadoCifra"].Value),
            Texto(textos["CsvColNif"].Value), Texto(textos["CsvColRazonSocial"].Value), Texto(textos["CsvColEmpresa"].Value),
            Texto(textos["CsvColConcepto"].Value), Texto(textos["CsvColUnidades"].Value), Texto(textos["CsvColPrecio"].Value),
            Texto(textos["CsvColImporte"].Value), Texto(textos["CsvColTipoIva"].Value), Texto(textos["CsvColMoneda"].Value),
            Texto(textos["CsvColEstadoCobro"].Value), Texto(textos["CsvColReferencia"].Value),
        ]);

        var documento = textos["CsvDocumento"].Value;
        var periodo = $"{modelo.Anyo:0000}-{modelo.Mes:00}";
        foreach (var empresa in modelo.Empresas)
        {
            var cobro = cobros.De(empresa.TenantId);
            var estadoCobro = EstadoCobroTexto(cobro?.Estado ?? EstadoCobroLiquidacion.Pendiente, textos);
            foreach (var concepto in empresa.Conceptos)
            {
                Linea(texto,
                [
                    Texto(documento), Texto(periodo), Texto(EstadoCifraTexto(empresa.Estado, textos)),
                    // NIF y razón social de la Empresa contraparte receptora: el modelo de la pantalla no los tiene, y no se
                    // inventan; van vacíos hasta que la liquidación los lea de la Empresa propia del Tenant beneficiario.
                    string.Empty, string.Empty,
                    Texto(empresa.Nombre),
                    Texto(textos[$"CsvConcepto{concepto.Concepto}"].Value),
                    concepto.Unidades is { } unidades ? unidades.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    Numero(concepto.Estado == EstadoTarifaConcepto.Cobra ? concepto.Precio : null),
                    Numero(concepto.Importe),
                    // El tipo de IVA lo decide el emisor en su programa de facturación: la tarifa no lo lleva.
                    string.Empty,
                    Texto(concepto.Moneda ?? string.Empty),
                    Texto(estadoCobro), Texto(cobro?.ReferenciaFacturaEmisor ?? string.Empty),
                ]);
            }
        }

        var preambulo = Encoding.UTF8.GetPreamble();
        var cuerpo = Encoding.UTF8.GetBytes(texto.ToString());
        return [.. preambulo, .. cuerpo];
    }

    public static string EstadoCobroTexto(EstadoCobroLiquidacion estado, IStringLocalizer<Recursos.TextosFacturacion> textos) => estado switch
    {
        EstadoCobroLiquidacion.Cobrada => textos["CobroCobrada"].Value,
        EstadoCobroLiquidacion.Impagada => textos["CobroImpagada"].Value,
        _ => textos["CobroPendiente"].Value,
    };

    private static string EstadoCifraTexto(EstadoCifraMensual estado, IStringLocalizer<Recursos.TextosFacturacion> textos) => estado switch
    {
        EstadoCifraMensual.Existe => textos["EstadoCerrado"].Value,
        EstadoCifraMensual.Estimado => textos["EstadoSinCerrar"].Value,
        _ => textos["EstadoEnCurso"].Value,
    };

    private static string Numero(decimal? valor) => valor is { } v ? v.ToString("0.00", FormatoNumero) : string.Empty;

    private static void Linea(StringBuilder destino, IReadOnlyList<string> celdas)
    {
        destino.Append(string.Join(Separador, celdas));
        destino.Append("\r\n");
    }

    /// <summary>Un texto: neutraliza fórmulas y entrecomilla si lleva separador, comillas o saltos de línea.</summary>
    public static string Texto(string valor)
    {
        if (valor.Length > 0 && valor[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            valor = "'" + valor;
        return valor.IndexOfAny([Separador, '"', '\r', '\n']) >= 0
            ? "\"" + valor.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : valor;
    }
}
