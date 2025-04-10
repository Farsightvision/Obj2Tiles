const util = require('util');
const exec = util.promisify(require('child_process').exec);

async function publishDotnet() {
    const command = `dotnet publish ./Obj2Tiles/Obj2Tiles.csproj -c Release -r win-x64 --self-contained true `
        + `/p:PublishSingleFile=true `
        + `/p:IncludeAllContentForSelfExtract=true `
        + `/p:PublishTrimmed=false `
        + `/p:EnableCompressionInSingleFile=true `
        + `/p:IncludeNativeLibrariesForSelfExtract=true`;

    console.log('Running command:\n' + command + '\n');

    try {
        const { stdout, stderr } = await exec(command);

        // if (stdout) console.log(`STDOUT:\n${stdout}`);
        // if (stderr) console.error(`STDERR:\n${stderr}`);
    } catch (err) {
        console.error('Error:\n${err.message}`);
        if (err.stdout) console.log(`STDOUT:\n${err.stdout}`);
        if (err.stderr) console.error(`STDERR:\n${err.stderr}`);
    }
}

publishDotnet();
