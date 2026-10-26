namespace CaeManager.Application.Integraciones;

/// <summary>Comprueba los resultados de autenticación (SPF, DKIM, DMARC) que el buzón receptor de
/// Microsoft 365 conserva en las cabeceras que devuelve Graph. No consulta DNS: solo lee lo que ya llega.
/// La ausencia, ambigüedad o un resultado no reconocido nunca otorgan confianza de Plataforma CAE.
/// La cabecera de Microsoft 365 no lleva authserv-id, así que por su contenido no se distingue de una
/// que escriba el propio remitente. Por eso se exige que haya exactamente una: Exchange Online Protection
/// sella la suya en el correo que entra de Internet, y una segunda puesta por el remitente deja el mensaje
/// sin confianza en vez de elegir la que diga pass. Además, si Exchange marca el mensaje como no anónimo
/// (<c>X-MS-Exchange-Organization-AuthAs</c>, cabecera que Exchange no acepta desde fuera), no llegó por
/// la entrada de Internet y la cabecera de resultados puede no ser la de Exchange Online Protection.</summary>
public static class AutenticacionCorreoPlataforma
{
    public static bool EsValida(MensajeGraphDto mensaje)
    {
        var dominio = ResolucionProveedorPlataformaCaeService.ExtraerDominioCorreo(mensaje.Remitente);
        var cabeceras = mensaje.Cabeceras ?? [];
        var resultadosAutenticacion = cabeceras
            .Where(c => c.Nombre.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dominio is null || resultadosAutenticacion.Length != 1 || NoEntroPorInternet(cabeceras))
            return false;

        // Microsoft 365 nunca entrecomilla valores en esta cabecera; unas comillas solo pueden venir de
        // texto del remitente copiado dentro (p. ej. una parte local entrecomillada con ';' que inyecte
        // un segmento «spf=pass») y el troceo simple de abajo no las interpreta.
        var valor = resultadosAutenticacion[0].Valor;
        if (valor.Contains('"'))
            return false;

        var resultados = valor.Split(';')
            .Select(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var dmarc = resultados.Where(r => r.Length > 0 && r[0].StartsWith("dmarc=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dmarc.Length > 1)
            return false;
        if (dmarc.Length == 1 && !SinPoliticaDmarc(dmarc[0]))
            return EsPass(dmarc[0], "dmarc") && TieneDominio(dmarc[0], "header.from", dominio);

        // Sin política DMARC (o sin resultado DMARC): SPF con smtp.mailfrom alineado o DKIM firmado
        // por el propio dominio del remitente. Igualdad estricta, sin dominio organizativo.
        return resultados.Any(r => EsPass(r, "spf") && TieneDominio(r, "smtp.mailfrom", dominio))
            || resultados.Any(r => EsPass(r, "dkim") && TieneDominio(r, "header.d", dominio));
    }

    private static bool NoEntroPorInternet(IReadOnlyList<CabeceraCorreoDto> cabeceras) =>
        cabeceras.Any(c => c.Nombre.Equals("X-MS-Exchange-Organization-AuthAs", StringComparison.OrdinalIgnoreCase)
            && !c.Valor.Trim().Equals("Anonymous", StringComparison.OrdinalIgnoreCase));

    // RFC 8601 § 2.7.1: «dmarc=none» significa que el dominio no publica política DMARC.
    // Microsoft 365 emite «bestguesspass» en el mismo caso. Ninguno de los dos es un pass:
    // solo dan paso a exigir SPF alineado o DKIM del propio dominio, como si no hubiera DMARC.
    private static bool SinPoliticaDmarc(string[] resultado) =>
        resultado[0].Equals("dmarc=none", StringComparison.OrdinalIgnoreCase)
        || resultado[0].Equals("dmarc=bestguesspass", StringComparison.OrdinalIgnoreCase);

    private static bool EsPass(string[] resultado, string metodo) =>
        resultado.Length > 0 && resultado[0].Equals($"{metodo}=pass", StringComparison.OrdinalIgnoreCase);

    private static bool TieneDominio(string[] resultado, string propiedad, string dominio)
    {
        var valores = resultado.Where(t => t.StartsWith($"{propiedad}=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (valores.Length != 1)
            return false;
        var valor = valores[0][(propiedad.Length + 1)..];
        if (propiedad == "smtp.mailfrom" && valor.Contains('@'))
            valor = valor[(valor.LastIndexOf('@') + 1)..];
        return valor.Equals(dominio, StringComparison.OrdinalIgnoreCase);
    }
}
