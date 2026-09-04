// SyntheticWav moved to Flower.DeviceChecks, which needs it to build the same
// fixtures on a phone, where no test framework exists. It is still the same
// generator every test here has always used, so it keeps the same name rather
// than making a dozen files say where it lives now.
global using SyntheticWav = Flower.DeviceChecks.SyntheticWav;
