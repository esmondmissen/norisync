pipeline {
    agent {
        dockerfile {
            filename 'Dockerfile'
            args     '-u 1000:1000 -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/.nuget'
        }
    }

    environment {
        DOTNET_VERSION              = '10.0'
        DOTNET_NOLOGO               = 'true'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        CONFIGURATION               = 'Release'
        PLUGIN_NAME                 = 'Jellyfin.Plugin.NoriSync'
        PLUGIN_VERSION              = '1.0.0'
        // owner/repo of the public GitHub fallback. Empty skips the GitHub stage entirely.
        GITHUB_REPO                 = 'esmondmissen/norisync'
    }

    options {
        timestamps()
        buildDiscarder(logRotator(numToKeepStr: '20', artifactNumToKeepStr: '10'))
        timeout(time: 20, unit: 'MINUTES')
    }

    triggers {
        pollSCM('H/5 * * * *')
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
                sh "git config --global --add safe.directory '${env.WORKSPACE}'"
            }
        }

        stage('Restore') {
            steps {
                sh 'dotnet restore ${PLUGIN_NAME}.sln --configfile nuget.config'
            }
        }

        stage('Build') {
            steps {
                sh '''
                    BUILD_VERSION="${PLUGIN_VERSION}.${BUILD_NUMBER}"

                    dotnet build ${PLUGIN_NAME}.sln \
                        --configuration ${CONFIGURATION} \
                        --no-restore

                    dotnet publish ${PLUGIN_NAME}/${PLUGIN_NAME}.csproj \
                        --configuration ${CONFIGURATION} --no-self-contained \
                        -o artifacts/publish \
                        -p:Version=${BUILD_VERSION} \
                        -p:AssemblyVersion=${BUILD_VERSION} \
                        -p:FileVersion=${BUILD_VERSION}
                '''
            }
        }

        stage('Package') {
            steps {
                sh '''
                    BUILD_VERSION="${PLUGIN_VERSION}.${BUILD_NUMBER}"
                    OUT=artifacts
                    BIN=${OUT}/publish
                    STAGE=${OUT}/stage
                    rm -rf ${STAGE} ${OUT}/*.zip ${OUT}/*.md5 && mkdir -p ${STAGE}

                    # Whitelist only DLLs not already loaded by the Jellyfin host
                    # process. Microsoft.Data.Sqlite, SQLitePCLRaw.*, TagLibSharp etc.
                    # are provided by Jellyfin and resolve at runtime to the host's
                    # already-loaded copies — shipping our own would just bloat the zip.
                    cp ${BIN}/Jellyfin.Plugin.NoriSync.dll ${STAGE}/
                    cp ${BIN}/FftSharp.dll ${STAGE}/
                    cp ${BIN}/MessagePack.dll ${STAGE}/
                    cp ${BIN}/MessagePack.Annotations.dll ${STAGE}/
                    cp ${BIN}/Microsoft.NET.StringTools.dll ${STAGE}/
                    cp image.jpg ${STAGE}/

                    # targetAbi is the FLOOR of the supported range, not a pin:
                    # Jellyfin treats it as a minimum, so this one build is
                    # offered to 10.11.x and 12.x servers alike.
                    cat > ${STAGE}/meta.json <<EOF
{
    "name": "NoriSync",
    "guid": "11c1ffe5-e4de-4cbb-a30c-c2870eb81603",
    "version": "${BUILD_VERSION}",
    "targetAbi": "10.11.0.0",
    "framework": "net9.0",
    "overview": "Sync local tracks with Jellyfin",
    "description": "A Jellyfin plugin for synchronizing Nori with your Jellyfin server library.",
    "category": "General",
    "owner": "Esmond",
    "artifacts": ["Jellyfin.Plugin.NoriSync.dll"],
    "imagePath": "image.jpg"
}
EOF

                    ZIP="${PLUGIN_NAME}_${BUILD_VERSION}.zip"
                    (cd ${STAGE} && zip -r9 "../${ZIP}" .)
                    (cd ${OUT} && md5sum "${ZIP}" > "${ZIP}.md5")
                '''
            }
        }

        stage('Archive') {
            steps {
                archiveArtifacts artifacts: 'artifacts/*.zip, artifacts/*.md5', fingerprint: true
            }
        }

        stage('Release') {
            steps {
                withCredentials([usernamePassword(credentialsId: 'gitea_usr_pwd', usernameVariable: 'GU', passwordVariable: 'GP')]) {
                    sh '''
                        TAG="v${PLUGIN_VERSION}.${BUILD_NUMBER}"
                        SHA=$(git rev-parse HEAD)
                        API="http://chips:3000/api/v1/repos/Esmond/jellyfin-plugin-norisync"

                        BODY=$(jq -n \
                            --arg tag "$TAG" \
                            --arg sha "$SHA" \
                            --arg build "$BUILD_NUMBER" \
                            '{tag_name:$tag, name:$tag, target_commitish:$sha, body:("Build " + $build + " (" + $sha[0:7] + ")")}')

                        REL_ID=$(curl -sSf -u "${GU}:${GP}" \
                            -H 'Content-Type: application/json' \
                            -d "$BODY" \
                            "${API}/releases" | jq -r '.id')

                        for asset in artifacts/*.zip artifacts/*.zip.md5; do
                            [ -f "${asset}" ] || continue
                            curl -sSf -u "${GU}:${GP}" \
                                -F "attachment=@${asset}" \
                                "${API}/releases/${REL_ID}/assets?name=$(basename ${asset})"
                        done

                        echo "Published release ${TAG} (id=${REL_ID})"
                    '''
                }
            }
        }

        stage('Publish Manifest') {
            steps {
                withCredentials([usernamePassword(credentialsId: 'gitea_usr_pwd', usernameVariable: 'GU', passwordVariable: 'GP')]) {
                    sh '''
                        API="http://chips:3000/api/v1/repos/Esmond/jellyfin-plugin-norisync"
                        BASE="http://chips:3000/Esmond/jellyfin-plugin-norisync"
                        PUBLIC_BASE="https://git.missen.ca/Esmond/jellyfin-plugin-norisync"
                        MANIFEST_TAG="manifest"
                        GUID="11c1ffe5-e4de-4cbb-a30c-c2870eb81603"
                        TARGET_ABI="10.11.0.0"

                        RELEASES=$(curl -sSf -u "${GU}:${GP}" "${API}/releases?limit=50" | \
                            jq '[.[] | select(.tag_name != "'"${MANIFEST_TAG}"'")]')

                        # One zip per release, named for the release tag. Any other
                        # asset in a release (e.g. the short-lived per-ABI builds)
                        # is ignored by construction.
                        VERSIONS='[]'
                        for row in $(echo "$RELEASES" | jq -r '.[] | @base64'); do
                            REL=$(echo "$row" | base64 -d)
                            TAG=$(echo "$REL" | jq -r '.tag_name')
                            CREATED=$(echo "$REL" | jq -r '.created_at')
                            CREATED_UTC=$(date -u -d "${CREATED}" +"%Y-%m-%dT%H:%M:%SZ")
                            BODY=$(echo "$REL" | jq -r '.body // ""')
                            VER=${TAG#v}
                            ZIP_NAME="${PLUGIN_NAME}_${VER}.zip"
                            MD5=$(curl -sSf -u "${GU}:${GP}" "${BASE}/releases/download/${TAG}/${ZIP_NAME}.md5" 2>/dev/null | awk '{print $1}')
                            [ -z "$MD5" ] && continue
                            ENTRY=$(jq -n \
                                --arg v "$VER" \
                                --arg abi "$TARGET_ABI" \
                                --arg url "${PUBLIC_BASE}/releases/download/${TAG}/${ZIP_NAME}" \
                                --arg md5 "$MD5" \
                                --arg ts "$CREATED_UTC" \
                                --arg cl "$BODY" \
                                '{version:$v, changelog:$cl, targetAbi:$abi, sourceUrl:$url, checksum:$md5, timestamp:$ts}')
                            VERSIONS=$(echo "$VERSIONS" | jq ". + [${ENTRY}]")
                        done

                        VERSIONS=$(echo "$VERSIONS" | jq 'sort_by(.timestamp) | reverse')

                        jq -n \
                            --arg guid "$GUID" \
                            --argjson versions "$VERSIONS" \
                            '[{
                                guid:$guid,
                                name:"NoriSync",
                                description:"A Jellyfin plugin for synchronizing Nori with your Jellyfin server library.",
                                overview:"Sync local tracks with Jellyfin",
                                owner:"Esmond",
                                category:"General",
                                versions:$versions
                            }]' > manifest.json

                        CODE=$(curl -sS -u "${GU}:${GP}" -o rel.json -w '%{http_code}' "${API}/releases/tags/${MANIFEST_TAG}")
                        if [ "$CODE" != "200" ]; then
                            curl -sSf -u "${GU}:${GP}" \
                                -H 'Content-Type: application/json' \
                                -d "$(jq -n --arg t "$MANIFEST_TAG" '{tag_name:$t, name:"Plugin manifest", body:"Auto-generated Jellyfin plugin manifest"}')" \
                                -o rel.json "${API}/releases"
                        fi
                        REL_ID=$(jq -r '.id' rel.json)

                        # Clean up any stale per-RID manifests from the earlier split approach.
                        for stale in $(jq -r '.assets[]? | select(.name | startswith("manifest-")) | .id' rel.json); do
                            curl -sSf -u "${GU}:${GP}" -X DELETE "${API}/releases/${REL_ID}/assets/${stale}"
                        done

                        OLD=$(jq -r '.assets[]? | select(.name=="manifest.json") | .id' rel.json)
                        if [ -n "$OLD" ]; then
                            curl -sSf -u "${GU}:${GP}" -X DELETE "${API}/releases/${REL_ID}/assets/${OLD}"
                        fi

                        curl -sSf -u "${GU}:${GP}" \
                            -F "attachment=@manifest.json" \
                            "${API}/releases/${REL_ID}/assets?name=manifest.json"

                        echo "Manifest URL: ${BASE}/releases/download/${MANIFEST_TAG}/manifest.json"
                    '''
                }
            }
        }

        // Fallback plugin repository on GitHub, used by servers when git.missen.ca
        // is unreachable. Its manifest only lists releases hosted on GitHub, so it
        // never depends on Gitea being up. GitHub's code is a snapshot per release;
        // there is deliberately no Gitea push mirror.
        stage('Publish GitHub Mirror') {
            when { expression { return env.GITHUB_REPO?.trim() } }
            steps {
                // The mirror is best-effort: a GitHub failure marks the build
                // unstable but never undoes the Gitea release above.
                catchError(buildResult: 'UNSTABLE', stageResult: 'FAILURE') {
                    withCredentials([string(credentialsId: 'github_token', variable: 'GHT')]) {
                        sh '''
                            TAG="v${PLUGIN_VERSION}.${BUILD_NUMBER}"
                            SHA=$(git rev-parse HEAD)
                            API="https://api.github.com/repos/${GITHUB_REPO}"
                            UPLOADS="https://uploads.github.com/repos/${GITHUB_REPO}"
                            MANIFEST_TAG="manifest"
                            TARGET_ABI="10.11.0.0"

                            ghapi() {
                                curl -sSf \
                                    -H "Authorization: Bearer ${GHT}" \
                                    -H 'Accept: application/vnd.github+json' \
                                    -H 'X-GitHub-Api-Version: 2022-11-28' \
                                    "$@"
                            }

                            GH_URL="https://x-access-token:${GHT}@github.com/${GITHUB_REPO}.git"

                            # GitHub gets the released code, not Gitea's history: each build
                            # commits this checkout's tree on top of the previous GitHub
                            # snapshot, so GitHub's history is one commit per release.
                            export GIT_AUTHOR_NAME="$(git log -1 --format=%an HEAD)"
                            export GIT_AUTHOR_EMAIL="$(git log -1 --format=%ae HEAD)"
                            export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME"
                            export GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"
                            if [ -n "$(git ls-remote --heads "$GH_URL" master)" ]; then
                                git fetch --quiet "$GH_URL" master
                                SNAP=$(git commit-tree "HEAD^{tree}" -p FETCH_HEAD -m "NoriSync ${TAG}")
                            else
                                SNAP=$(git commit-tree "HEAD^{tree}" -m "NoriSync ${TAG}")
                            fi
                            git push --quiet "$GH_URL" "${SNAP}:refs/heads/master" "${SNAP}:refs/tags/${TAG}"

                            BODY=$(jq -n \
                                --arg tag "$TAG" \
                                --arg sha "$SHA" \
                                --arg build "$BUILD_NUMBER" \
                                '{tag_name:$tag, name:$tag, body:("Build " + $build + " (" + $sha[0:7] + ")")}')

                            REL_ID=$(ghapi -d "$BODY" "${API}/releases" | jq -r '.id')

                            for asset in artifacts/*.zip artifacts/*.zip.md5; do
                                [ -f "${asset}" ] || continue
                                ghapi -H 'Content-Type: application/octet-stream' \
                                    --data-binary "@${asset}" \
                                    "${UPLOADS}/releases/${REL_ID}/assets?name=$(basename ${asset})" > /dev/null
                            done

                            echo "Published GitHub release ${TAG} (id=${REL_ID})"

                            RELEASES=$(ghapi "${API}/releases?per_page=50" | \
                                jq --arg m "$MANIFEST_TAG" '[.[] | select(.tag_name != $m and (.draft | not))]')

                            VERSIONS='[]'
                            for row in $(echo "$RELEASES" | jq -r '.[] | @base64'); do
                                REL=$(echo "$row" | base64 -d)
                                REL_TAG=$(echo "$REL" | jq -r '.tag_name')
                                # published_at, not created_at: on GitHub created_at is the
                                # commit's date, not the release's.
                                PUBLISHED=$(echo "$REL" | jq -r '.published_at')
                                REL_BODY=$(echo "$REL" | jq -r '.body // ""')
                                VER=${REL_TAG#v}
                                ZIP_NAME="${PLUGIN_NAME}_${VER}.zip"
                                ZIP_URL=$(echo "$REL" | jq -r --arg n "$ZIP_NAME" '.assets[] | select(.name == $n) | .browser_download_url')
                                MD5_URL=$(echo "$REL" | jq -r --arg n "${ZIP_NAME}.md5" '.assets[] | select(.name == $n) | .browser_download_url')
                                [ -z "$ZIP_URL" ] && continue
                                [ -z "$MD5_URL" ] && continue
                                MD5=$(curl -sSfL "$MD5_URL" 2>/dev/null | awk '{print $1}')
                                [ -z "$MD5" ] && continue
                                ENTRY=$(jq -n \
                                    --arg v "$VER" \
                                    --arg abi "$TARGET_ABI" \
                                    --arg url "$ZIP_URL" \
                                    --arg md5 "$MD5" \
                                    --arg ts "$PUBLISHED" \
                                    --arg cl "$REL_BODY" \
                                    '{version:$v, changelog:$cl, targetAbi:$abi, sourceUrl:$url, checksum:$md5, timestamp:$ts}')
                                VERSIONS=$(echo "$VERSIONS" | jq ". + [${ENTRY}]")
                            done

                            VERSIONS=$(echo "$VERSIONS" | jq 'sort_by(.timestamp) | reverse')

                            jq -n \
                                --arg guid "11c1ffe5-e4de-4cbb-a30c-c2870eb81603" \
                                --argjson versions "$VERSIONS" \
                                '[{
                                    guid:$guid,
                                    name:"NoriSync",
                                    description:"A Jellyfin plugin for synchronizing Nori with your Jellyfin server library.",
                                    overview:"Sync local tracks with Jellyfin",
                                    owner:"Esmond",
                                    category:"General",
                                    versions:$versions
                                }]' > gh-manifest.json

                            CODE=$(curl -sS -H "Authorization: Bearer ${GHT}" -H 'Accept: application/vnd.github+json' \
                                -o gh-rel.json -w '%{http_code}' "${API}/releases/tags/${MANIFEST_TAG}")
                            if [ "$CODE" != "200" ]; then
                                # make_latest=false keeps GitHub's "Latest" badge on real builds.
                                ghapi -d "$(jq -n --arg t "$MANIFEST_TAG" --arg sha "$SNAP" \
                                        '{tag_name:$t, target_commitish:$sha, name:"Plugin manifest", body:"Auto-generated Jellyfin plugin manifest", make_latest:"false"}')" \
                                    -o gh-rel.json "${API}/releases"
                            fi
                            MANIFEST_REL_ID=$(jq -r '.id' gh-rel.json)

                            OLD=$(jq -r '.assets[]? | select(.name=="manifest.json") | .id' gh-rel.json)
                            if [ -n "$OLD" ]; then
                                ghapi -X DELETE "${API}/releases/assets/${OLD}"
                            fi

                            ghapi -H 'Content-Type: application/octet-stream' \
                                --data-binary @gh-manifest.json \
                                "${UPLOADS}/releases/${MANIFEST_REL_ID}/assets?name=manifest.json" > /dev/null

                            echo "GitHub manifest URL: https://github.com/${GITHUB_REPO}/releases/download/${MANIFEST_TAG}/manifest.json"
                        '''
                    }
                }
            }
        }
    }

    post {
        failure { echo "Build ${env.BUILD_NUMBER} failed on ${env.BRANCH_NAME ?: 'master'}" }
        cleanup { cleanWs() }
    }
}
