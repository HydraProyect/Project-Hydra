using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.ContextoRls;

/// <summary>
/// Registro y lectura de la clave HMAC del contexto RLS firmado (P6).
///
/// <para>
/// <b>Quién registra.</b> Solo la identidad propietaria puede escribir en
/// <c>app_privado.claves_contexto</c>. La registra el migrador
/// (<c>MigrarBaseDeDatosAsync</c> en <c>Program.cs</c>), que es el único
/// proceso de staging y producción con esa credencial: el contenedor
/// <c>app</c> no la recibe (#882). Cada ejecución del migrador registra una
/// clave nueva de <see cref="VigenciaPorDefecto"/>, así que rota en cada
/// despliegue o al relanzar el migrador a mano.
/// </para>
///
/// <para>
/// <b>Cómo llega al proceso web.</b> Junto a los rellenos HMAC que valida la
/// base se guarda la clave cifrada con DataProtection
/// (<see cref="Proposito"/>). <c>cae_app_runtime</c> no puede leer la tabla;
/// solo puede llamar a <c>app_claves_contexto_protegidas()</c>, que devuelve
/// el cifrado y nunca los rellenos. Descifrarlo exige el anillo de claves de
/// DataProtection, que vive en el disco que comparten migrador y app y no en
/// la base: una inyección SQL con la credencial de tráfico obtiene texto
/// cifrado y nada más.
/// </para>
/// </summary>
public static class ClaveContextoRls
{
    /// <summary>Propósito de DataProtection: aísla este cifrado de cualquier otro uso del anillo.</summary>
    public const string Proposito = "CaeManager.ContextoRls.ClaveHmac.v1";

    public static readonly TimeSpan VigenciaPorDefecto = TimeSpan.FromDays(30);

    /// <summary>Por debajo de este margen, <c>/salud</c> pasa a Degraded.</summary>
    public static readonly TimeSpan AvisoCaducidad = TimeSpan.FromDays(7);

    /// <summary>
    /// Genera una clave, la registra con la conexión propietaria
    /// <paramref name="cadenaPropietaria"/> y borra las caducadas. Devuelve la
    /// clave en claro para quien la necesite en el mismo proceso.
    /// </summary>
    public static async Task<ClaveContextoLeida> RegistrarAsync(
        string cadenaPropietaria, IDataProtectionProvider proveedor, TimeSpan vigencia, CancellationToken ct)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var (ipad, opad) = TokenContextoRls.Rellenos(bytes);
        var protegida = proveedor.CreateProtector(Proposito).Protect(bytes);
        var id = Guid.NewGuid();

        // Conexión propietaria cruda, al margen de EF y de los interceptores a
        // propósito (ver ConexionesFueraDelInterceptorTests): no lee ni escribe
        // filas de ningún Tenant, solo la tabla de claves.
        await using var conexionPropietaria = new NpgsqlConnection(cadenaPropietaria);
        await conexionPropietaria.OpenAsync(ct);
        await using var comando = conexionPropietaria.CreateCommand();
        comando.CommandText =
            "INSERT INTO app_privado.claves_contexto (id, ipad, opad, clave_protegida, valida_hasta) " +
            "VALUES (@id, @ipad, @opad, @protegida, now() + @vigencia) RETURNING valida_hasta; ";
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("ipad", ipad);
        comando.Parameters.AddWithValue("opad", opad);
        comando.Parameters.AddWithValue("protegida", protegida);
        comando.Parameters.AddWithValue("vigencia", vigencia);
        var validaHasta = (DateTime)(await comando.ExecuteScalarAsync(ct))!;

        await using var limpieza = conexionPropietaria.CreateCommand();
        limpieza.CommandText = "DELETE FROM app_privado.claves_contexto WHERE valida_hasta < now();";
        await limpieza.ExecuteNonQueryAsync(ct);

        return new ClaveContextoLeida(id, bytes, new DateTimeOffset(validaHasta, TimeSpan.Zero));
    }

    /// <summary>
    /// La clave vigente más reciente que este proceso sabe descifrar, leída por
    /// <paramref name="conexion"/> (la de tráfico) con
    /// <c>app_claves_contexto_protegidas()</c>. <c>null</c> si no hay ninguna
    /// vigente o ninguna se deja descifrar con este anillo: el llamante no
    /// firma (falla cerrado).
    /// </summary>
    public static async Task<ClaveContextoLeida?> LeerVigenteAsync(
        NpgsqlConnection conexion, IDataProtectionProvider proveedor, CancellationToken ct)
    {
        var protector = proveedor.CreateProtector(Proposito);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT id, clave_protegida, valida_hasta FROM public.app_claves_contexto_protegidas();";
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var protegida = lector.GetFieldValue<byte[]>(1);
            byte[] bytes;
            try
            {
                bytes = protector.Unprotect(protegida);
            }
            catch (CryptographicException)
            {
                // Cifrada con otro anillo (otro entorno, anillo perdido o
                // rotado fuera de plazo). Se prueba la siguiente.
                continue;
            }

            if (bytes.Length != 32)
                continue;

            return new ClaveContextoLeida(
                lector.GetGuid(0), bytes, new DateTimeOffset(lector.GetFieldValue<DateTime>(2), TimeSpan.Zero));
        }

        return null;
    }
}

/// <summary>Una clave del contexto RLS en claro, con su identificador y su caducidad en la base.</summary>
public sealed record ClaveContextoLeida(Guid Id, byte[] Bytes, DateTimeOffset ValidaHasta);
