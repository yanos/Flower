using System;
using System.IO;
using System.Linq;

using Makaretu.Dns;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// What a discovered server is called, from the bytes it actually put on the
// wire. DNS-SD instance labels are UTF-8 (RFC 6763 s4.1.1), so a server named
// "Café" - or "Basement NAS", which is the same problem with a space - is an
// ordinary server, and MakaretuMdnsBackend is where its announcement turns
// into the string the rest of the app keys, logs and displays it by.
//
// The trap is that Makaretu's DomainName.ToString() is not that string: it
// renders the labels back into DNS master-file presentation form, where every
// byte outside printable ASCII becomes a decimal \DDD escape. These go through
// a real WireWriter/WireReader round-trip rather than constructing the name
// directly, because the escaping only shows up once something has decided how
// to print decoded labels - which is exactly the step the app used to take.
public class MdnsInstanceNameTests
{
    private const string ServiceType = "_flowersync._tcp";

    // The name as it arrives from the LAN: encoded to wire bytes the way an
    // advertising server does, then read back the way the browsing client does.
    private static DomainName Announced(string instanceName)
    {
        var stream = new MemoryStream();
        new WireWriter(stream).WriteDomainName(new ServiceProfile(instanceName, ServiceType, 4533).FullyQualifiedName);
        return new WireReader(new MemoryStream(stream.ToArray())).ReadDomainName();
    }

    [Theory]
    [InlineData("Café")]
    [InlineData("Basement NAS")]      // a space, which escapes as \032 - not an accent in sight
    [InlineData("Mr Téléphone")]
    [InlineData("太郎's Mac")]         // beyond Latin-1, where the escape isn't even reversible
    [InlineData("Basement-NAS")]
    // Both backends also have to key the same server the same way, or one
    // machine is two devices depending on which platform is looking. Flower.iOS
    // builds "<instance>.<type>.local" by hand from the unescaped name Bonjour
    // hands it (BonjourMdnsBackend.OnBrowseReply) and cannot be referenced from
    // here, so the format asserted below is also what has to keep matching it.
    public void A_server_is_named_what_its_owner_called_it(string instanceName)
    {
        Assert.Equal(
            $"{instanceName}.{ServiceType}.local",
            MakaretuMdnsBackend.InstanceNameOf(Announced(instanceName)));
    }

    // The reason the helper exists at all. If a Makaretu release ever makes
    // ToString() safe to use directly this fails, which is the good outcome -
    // it says the workaround can go.
    [Fact]
    public void The_presentation_form_that_used_to_be_used_is_still_not_the_name()
    {
        Assert.Equal(@"Caf\233._flowersync._tcp.local", Announced("Café").ToString());
        Assert.Equal(@"Basement\032NAS._flowersync._tcp.local", Announced("Basement NAS").ToString());
    }
}
