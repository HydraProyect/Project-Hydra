namespace CaeManager.Application.Common;

/// <summary>
/// Convierte la imagen subida como logo de un Tenant en el único formato que se sirve: PNG cuadrado
/// de <see cref="LadoPixeles"/> px, reencodado desde el mapa de bits (contrato del selector de Tenant,
/// § 4.1.2 e invariante I10). Ningún byte del original llega a la salida: ni EXIF, ni ICC, ni chunks
/// de texto. El formato se decide por la cabecera, nunca por la extensión ni el Content-Type.
/// </summary>
public interface IConversorLogoTenantService
{
    const int LadoPixeles = 256;

    /// <summary>
    /// PNG de 256 × 256 con margen transparente (sin recortar). Lanza
    /// <see cref="LogoTenantNoAdmitidoException"/> si la imagen no es PNG ni JPEG, supera el límite de
    /// dimensiones leído en la cabecera o no decodifica.
    /// </summary>
    byte[] ConvertirAPng(byte[] imagenOriginal);
}

public enum MotivoLogoNoAdmitido
{
    FormatoNoAdmitido,
    DimensionesExcesivas,
    NoDecodificable
}

public sealed class LogoTenantNoAdmitidoException(MotivoLogoNoAdmitido motivo, string mensaje)
    : Exception(mensaje)
{
    public MotivoLogoNoAdmitido Motivo { get; } = motivo;
}
