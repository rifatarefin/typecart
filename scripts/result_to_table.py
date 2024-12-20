from collections import defaultdict
from natsort import natsorted


def main():
    total_lemmas = defaultdict(lambda: defaultdict(int))
    verified = defaultdict(lambda: defaultdict(int))
    with open('result.csv') as f:
        for line in f.readlines():
            parts = line.split(',')
            if parts[-1].strip().endswith('error'):
                continue
            if len(parts) != 9:
                continue
            current_verified = parts[6].split('/')
            if len(current_verified) != 2 or not current_verified[0].strip().isnumeric():
                continue
            total_lemmas[parts[0].strip()][parts[1].strip()] = int(current_verified[1].strip())
            verified[parts[0].strip()][parts[1].strip()] = int(current_verified[0].strip())
    for i in natsorted(verified.keys()):
        if total_lemmas[i]['-a 1 -p false -b true'] == 0 and ('/' not in i or int(i.split('/')[0]) not in [44, 111]):
            continue
        print(f"{i[:7]} & {total_lemmas[i]['-a 1 -p false -b true']} & {total_lemmas[i]['']} & {verified[i]['-p false -b true']} & {verified[i]['']} \\\\")

if __name__ == '__main__':
    main()
